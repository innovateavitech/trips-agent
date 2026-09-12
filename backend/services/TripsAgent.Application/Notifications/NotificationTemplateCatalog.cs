using TripsAgent.Domain.Notifications;

namespace TripsAgent.Application.Notifications;

/// <summary>
/// One version of one template, as the code defines it before it is seeded into the database.
/// </summary>
/// <param name="Key">Stable identifier. Never renamed: queued notifications point at it.</param>
/// <param name="Channel">Which channel this rendering is for.</param>
/// <param name="Locale">BCP-47 tag.</param>
/// <param name="Version">Bumped whenever anything below it changes, wording or layout.</param>
/// <param name="Audience">Whose brand wraps it, and therefore which layout it was built from.</param>
/// <param name="Subject">Subject line, with <c>{{token}}</c> placeholders.</param>
/// <param name="Html">The whole HTML document, layout included.</param>
/// <param name="Text">The whole plain-text body.</param>
/// <param name="Tokens">
/// Every token the bodies use beyond the brand ones, so a caller can be told it forgot one before
/// the message is queued rather than when the dispatcher fails to render it.
/// </param>
public sealed record NotificationTemplateDefinition(
    string Key,
    NotificationChannel Channel,
    string Locale,
    int Version,
    NotificationAudience Audience,
    string Subject,
    string Html,
    string Text,
    IReadOnlyList<string> Tokens);

/// <summary>
/// Every notification template the platform sends, in code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why code and not rows somebody edits.</b> Wording that a traveller receives is part of the
/// product, so it changes through a pull request like everything else: reviewable, revertible, and
/// impossible to break on a Friday night from a database console. <c>NotificationTemplateSeeder</c>
/// copies this into <c>notifications.notification_templates</c>, which is what the dispatcher reads
/// — so a notification can record the exact version it rendered from.
/// </para>
/// <para>
/// <b>Changing a template means bumping its version.</b> Versions are immutable once seeded; the
/// seeder inserts a new one and retires the old. Editing the string without touching the number
/// leaves production sending the old wording, because the row it already has is never rewritten.
/// </para>
/// <para>
/// <b>Two layouts, and which one a template uses is not a style choice.</b> Agency-facing mail is
/// ours to brand, and follows the house style already set by the verification email: no colours, no
/// images, no web fonts, so it reads correctly in every client with images blocked. Traveller-facing
/// mail carries the <i>agency's</i> name, logo, colour and contact details and never mentions Trips
/// — CLAUDE.md rule 4, and the reason the platform is worth paying for.
/// </para>
/// </remarks>
public static class NotificationTemplateCatalog
{
    /// <summary>The name agency-facing mail uses for us. Never appears in traveller-facing mail.</summary>
    public const string ProductName = "Trips Agent";

    // ------------------------------------------------------------------ keys

    /// <summary>
    /// The code that proves a new account owns its email address. Rendered from here but sent at
    /// once by <c>VerificationCodeIssuer</c>, never queued: a queued row would hold the live code.
    /// </summary>
    public const string IdentityVerifyEmail = "identity.verify-email";

    /// <summary>
    /// The link that resets a password. Sent at once by <c>ForgotPasswordHandler</c>, never queued,
    /// for the same reason: a queued row would hold a working link.
    /// </summary>
    public const string IdentityPasswordReset = "identity.password-reset";

    /// <summary>A traveller's invoice and vouchers, attached as PDFs (#46).</summary>
    public const string DocumentsIssued = "documents.issued";

    /// <summary>A corrected invoice or voucher that replaces one the traveller already has (#46).</summary>
    public const string DocumentsReissued = "documents.reissued";

    /// <summary>An agency's KYB submission was approved.</summary>
    public const string KybApproved = "kyb.approved";

    /// <summary>An agency's KYB submission needs different documents.</summary>
    public const string KybRejected = "kyb.rejected";

    /// <summary>An agency's wallet top-up was credited.</summary>
    public const string WalletTopUpReceipt = "wallet.topup-receipt";

    /// <summary>One item of a traveller's booking is confirmed and ticketed (#42). Sent once per order line.</summary>
    public const string BookingConfirmed = "booking.confirmed";

    /// <summary>
    /// One item of a traveller's booking could not be ticketed and waits in the agent's resolution
    /// queue (#44). Says so plainly, and asks nothing of the traveller: the agency is the one who acts.
    /// </summary>
    public const string BookingNeedsAttention = "booking.needs-attention";

    /// <summary>
    /// An item that could not be ticketed was cancelled and its money went back (#43, #44). Names an
    /// amount only when it went back to the traveller's own card.
    /// </summary>
    public const string BookingRefundNotice = "booking.refund-notice";

    /// <summary>An agent's held booking is close to its ticket time limit (#38). Sent at T-60 and T-15 minutes.</summary>
    public const string BookingTimeLimitWarning = "booking.time-limit-warning";

    /// <summary>An agent's held booking passed its ticket time limit before it was issued (#38).</summary>
    public const string BookingExpired = "booking.expired";

    /// <summary>
    /// A seat freed up on a departure somebody is waiting for (build plan F6, plan §3 job 10). The
    /// offer has a deadline; when it passes, job 10 rolls it on to the next person.
    /// </summary>
    public const string DepartureWaitlistOffer = "departure.waitlist-offer";

    /// <summary>An installment on a departure booking falls due soon, or is overdue (plan §3 job 11).</summary>
    /// <remarks>No card is ever charged from a reminder — the MVP leaves automatic charging out.</remarks>
    public const string DepartureInstallmentReminder = "departure.installment-reminder";

    /// <summary>An installment is past its grace period, so the agency has to chase it (job 11).</summary>
    public const string DepartureInstallmentOverdue = "departure.installment-overdue";

    /// <summary>A departure the traveller has a seat on was called off (build plan decision 12).</summary>
    public const string DepartureCancelled = "departure.cancelled";

    /// <summary>
    /// The link a traveller manages their own booking with, sent once their payment has gone through
    /// (build plan F5, decision 21 — there are no traveller accounts to sign in to).
    /// </summary>
    public const string BookingManageLink = "booking.manage-link";

    /// <summary>
    /// A quote the agency has sent to one of its own customers (#62). The customer's link to it is the
    /// whole point of the email, and it goes to the agency's own storefront — never to ours.
    /// </summary>
    public const string CrmQuoteSent = "crm.quote-sent";

    /// <summary>A follow-up task that has fallen due, to the person at the agency who owns it (#62).</summary>
    public const string CrmTaskDue = "crm.task-due";

    // ------------------------------------------------------------------ brand tokens

    /// <summary>Whose mail this appears to be: the agency's trading name, or <see cref="ProductName"/>.</summary>
    public const string BrandNameToken = "brandName";

    /// <summary>The agency's primary colour. Only traveller-facing layouts use it.</summary>
    public const string BrandColorToken = "brandColor";

    /// <summary>The agency's logo URL, or empty when it has not uploaded one.</summary>
    public const string BrandLogoUrlToken = "brandLogoUrl";

    /// <summary>The agency's contact details for the footer, or empty.</summary>
    public const string BrandContactToken = "brandContact";

    /// <summary>Who the message is addressed to.</summary>
    public const string RecipientNameToken = "recipientName";

    /// <summary>Tokens the renderer always supplies, so a template may use them without declaring them.</summary>
    public static readonly IReadOnlyList<string> BrandTokens =
    [
        BrandNameToken,
        BrandColorToken,
        BrandLogoUrlToken,
        BrandContactToken,
        RecipientNameToken,
    ];

    /// <summary>Every template version this build knows about.</summary>
    public static readonly IReadOnlyList<NotificationTemplateDefinition> All =
    [
        // Rendered from here like everything else, so the wording is versioned and reviewed in the
        // same place — but sent synchronously and never queued. See the key constants.
        AgencyFacing(
            IdentityVerifyEmail,
            version: 1,
            subject: "{{code}} is your {{brandName}} verification code",
            tokens: ["code", "minutes"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>Use this code to verify your email address and finish setting up your
                     {{brandName}} account:</p>
                  <p style="font-size:28px;font-weight:bold;letter-spacing:4px">{{code}}</p>
                  <p>The code expires in {{minutes}} minutes and can be used once.</p>
                  <p>If you did not try to create an account, you can ignore this email — nothing
                     will happen without the code.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  Use this code to verify your email address and finish setting up your {{brandName}} account:

                      {{code}}

                  The code expires in {{minutes}} minutes and can be used once.

                  If you did not try to create an account, you can ignore this email — nothing will happen without the code.
                  """),

        AgencyFacing(
            IdentityPasswordReset,
            version: 1,
            subject: "Reset your {{brandName}} password",
            tokens: ["resetUrl", "minutes"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>Use this link to choose a new password:</p>
                  <p><a href="{{resetUrl}}">Reset your password</a></p>
                  <p>The link works once and expires in {{minutes}} minutes.</p>
                  <p>If you did not ask for this, you can ignore this email — your password has
                     not changed, and nobody can change it without this link.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  Use this link to choose a new password:

                      {{resetUrl}}

                  The link works once and expires in {{minutes}} minutes.

                  If you did not ask for this, you can ignore this email — your password has not
                  changed, and nobody can change it without this link.
                  """),

        AgencyFacing(
            KybApproved,
            version: 1,
            subject: "{{businessName}} is verified",
            tokens: ["businessName"],
            html: """
                  <p>Good news — {{businessName}} is verified.</p>
                  <p>You can now add funds to your wallet and start booking.</p>
                  <p>Sign in to your {{brandName}} console to get started.</p>
                  """,
            text: """
                  Good news — {{businessName}} is verified.

                  You can now add funds to your wallet and start booking.

                  Sign in to your {{brandName}} console to get started.
                  """),

        AgencyFacing(
            KybRejected,
            version: 1,
            subject: "We need something else for {{businessName}}",
            tokens: ["businessName", "reason"],
            html: """
                  <p>We could not verify {{businessName}} yet.</p>
                  <p><strong>What we need:</strong></p>
                  <blockquote>{{reason}}</blockquote>
                  <p>Sign in to your {{brandName}} console, upload the corrected documents and
                     submit again. There is no limit on how many times you can try.</p>
                  """,
            text: """
                  We could not verify {{businessName}} yet.

                  What we need:

                      {{reason}}

                  Sign in to your {{brandName}} console, upload the corrected documents and submit
                  again. There is no limit on how many times you can try.
                  """),

        AgencyFacing(
            WalletTopUpReceipt,
            version: 1,
            subject: "Your wallet has been topped up — {{amount}}",
            tokens: ["amount", "reference"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>We've added <strong>{{amount}}</strong> to your wallet.</p>
                  <p>Reference: {{reference}}</p>
                  <p>You can see the full statement in your console.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  We've added {{amount}} to your wallet.

                  Reference: {{reference}}

                  You can see the full statement in your console.
                  """),

        // The ticket time limit (#38). To the agent, not the traveller: the agent is the one who can
        // still act — finish the booking, or rebook and refund — and whether and how to tell their own
        // customer is theirs to decide.
        AgencyFacing(
            BookingTimeLimitWarning,
            version: 1,
            subject: "{{minutesLeft}} minutes left to ticket {{bookingReference}}",
            tokens: ["bookingReference", "itinerarySummary", "minutesLeft", "deadline"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>The fare held for <strong>{{bookingReference}}</strong> ({{itinerarySummary}}) must be
                     ticketed by <strong>{{deadline}}</strong> — about {{minutesLeft}} minutes from now.</p>
                  <p>After that the airline or operator releases the seats and the price is no longer
                     guaranteed. Open the booking in your {{brandName}} console to finish it.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  The fare held for {{bookingReference}} ({{itinerarySummary}}) must be ticketed by
                  {{deadline}} — about {{minutesLeft}} minutes from now.

                  After that the airline or operator releases the seats and the price is no longer
                  guaranteed. Open the booking in your {{brandName}} console to finish it.
                  """),

        AgencyFacing(
            BookingExpired,
            version: 1,
            subject: "{{bookingReference}} expired before it was ticketed",
            tokens: ["bookingReference", "itinerarySummary", "deadline"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>The fare held for <strong>{{bookingReference}}</strong> ({{itinerarySummary}}) was not
                     ticketed by its time limit, {{deadline}}, and the supplier has released it. No ticket
                     was issued.</p>
                  <p>It is waiting in your resolution queue, where you can rebook it or refund your
                     customer. Funds held in your wallet for the order are released once nothing else on
                     it still needs them.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  The fare held for {{bookingReference}} ({{itinerarySummary}}) was not ticketed by its
                  time limit, {{deadline}}, and the supplier has released it. No ticket was issued.

                  It is waiting in your resolution queue, where you can rebook it or refund your
                  customer. Funds held in your wallet for the order are released once nothing else on
                  it still needs them.
                  """),

        AgencyFacing(
            CrmTaskDue,
            version: 1,
            subject: "Follow-up due: {{taskTitle}}",
            tokens: ["taskTitle", "customerName", "dueAt"],
            html: """
                  <p>A follow-up you are looking after was due at {{dueAt}}.</p>
                  <table role="presentation" cellpadding="6" cellspacing="0">
                    <tr><td><strong>To do</strong></td><td>{{taskTitle}}</td></tr>
                    <tr><td><strong>Customer</strong></td><td>{{customerName}}</td></tr>
                  </table>
                  <p>Open your tasks in the console to mark it done, or move it if it can wait.</p>
                  """,
            text: """
                  A follow-up you are looking after was due at {{dueAt}}.

                      To do:    {{taskTitle}}
                      Customer: {{customerName}}

                  Open your tasks in the console to mark it done, or move it if it can wait.
                  """),

        // ------------------------------------------------------------------ traveller-facing
        // Everything below goes to the agency's own customer. It says the agency's name and
        // nothing else: a traveller who learns Trips exists has learned their agent's supplier,
        // which is the one thing the agent is paying us to hide.
        TravellerFacing(
            BookingConfirmed,
            version: 1,
            subject: "Your booking is confirmed — {{bookingReference}}",
            tokens: ["bookingReference", "itinerarySummary", "travellerNames", "departureDate"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>Your booking with {{brandName}} is confirmed.</p>
                  <table role="presentation" cellpadding="6" cellspacing="0">
                    <tr><td><strong>Reference</strong></td><td>{{bookingReference}}</td></tr>
                    <tr><td><strong>Trip</strong></td><td>{{itinerarySummary}}</td></tr>
                    <tr><td><strong>Departing</strong></td><td>{{departureDate}}</td></tr>
                    <tr><td><strong>Travellers</strong></td><td>{{travellerNames}}</td></tr>
                  </table>
                  <p>Your ticket is attached to this booking in your confirmation documents. Please
                     check that every traveller's name matches their passport exactly.</p>
                  <p>Reply to this email if anything looks wrong.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  Your booking with {{brandName}} is confirmed.

                  Reference:   {{bookingReference}}
                  Trip:        {{itinerarySummary}}
                  Departing:   {{departureDate}}
                  Travellers:  {{travellerNames}}

                  Please check that every traveller's name matches their passport exactly.

                  Reply to this email if anything looks wrong.
                  """),

        // Version 2 (#44): the pipeline's failed line needs the agency, not the traveller, to act —
        // version 1 asked the traveller to do something by a deadline. Plain and without alarm, and
        // with no reason: the pipeline's is written for the agent, and it names the supplier.
        TravellerFacing(
            BookingNeedsAttention,
            version: 2,
            subject: "One item in your booking needs attention — {{bookingReference}}",
            tokens: ["bookingReference", "itemTitle"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>One item in your booking with {{brandName}} needs attention: its ticket could not
                     be issued.</p>
                  <table role="presentation" cellpadding="6" cellspacing="0">
                    <tr><td><strong>Reference</strong></td><td>{{bookingReference}}</td></tr>
                    <tr><td><strong>Item</strong></td><td>{{itemTitle}}</td></tr>
                  </table>
                  <p>{{brandName}} is looking into it and will be in touch about what happens next. You
                     do not need to do anything right now.</p>
                  <p>Reply to this email if you have any questions.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  One item in your booking with {{brandName}} needs attention: its ticket could not be
                  issued.

                  Reference:  {{bookingReference}}
                  Item:       {{itemTitle}}

                  {{brandName}} is looking into it and will be in touch about what happens next. You do
                  not need to do anything right now.

                  Reply to this email if you have any questions.
                  """),

        // After a reversal (#43) or the agent's refund (#44). What it says about the money is
        // refundDetail, from BookingEmails.DescribeRefund: an amount only for a card refund, because
        // money returned to the agency's wallet is what the agency paid, never the traveller's price.
        TravellerFacing(
            BookingRefundNotice,
            version: 1,
            subject: "An item in your booking has been cancelled — {{bookingReference}}",
            tokens: ["bookingReference", "itemTitle", "refundDetail"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>One item in your booking with {{brandName}} could not be ticketed, so it has been
                     cancelled.</p>
                  <table role="presentation" cellpadding="6" cellspacing="0">
                    <tr><td><strong>Reference</strong></td><td>{{bookingReference}}</td></tr>
                    <tr><td><strong>Item</strong></td><td>{{itemTitle}}</td></tr>
                  </table>
                  <p>{{refundDetail}}</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  One item in your booking with {{brandName}} could not be ticketed, so it has been
                  cancelled.

                  Reference:  {{bookingReference}}
                  Item:       {{itemTitle}}

                  {{refundDetail}}
                  """),

        // The PDFs themselves travel as attachments (Notification.AttachmentAssetIds), not as a
        // link: every link we could put here today would be on our own domain.
        TravellerFacing(
            DocumentsIssued,
            version: 1,
            subject: "Your booking documents — {{bookingReference}}",
            tokens: ["bookingReference", "itinerarySummary", "documentList"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>Here are your documents from {{brandName}} for booking
                     <strong>{{bookingReference}}</strong> ({{itinerarySummary}}).</p>
                  <p>They are attached to this email as PDF files: {{documentList}}.</p>
                  <p>Keep the voucher where you can find it on the day — it is what you show at
                     check-in or at the terminal.</p>
                  <p>Reply to this email if anything on them looks wrong.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  Here are your documents from {{brandName}} for booking {{bookingReference}}
                  ({{itinerarySummary}}).

                  They are attached to this email as PDF files: {{documentList}}.

                  Keep the voucher where you can find it on the day — it is what you show at
                  check-in or at the terminal.

                  Reply to this email if anything on them looks wrong.
                  """),

        TravellerFacing(
            DocumentsReissued,
            version: 1,
            subject: "Updated {{documentList}} — {{bookingReference}}",
            tokens: ["bookingReference", "itinerarySummary", "documentList"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>{{brandName}} has issued an updated {{documentList}} for booking
                     <strong>{{bookingReference}}</strong> ({{itinerarySummary}}).</p>
                  <p>It is attached to this email as a PDF, and it replaces the copy you were sent
                     before. Please use this one from now on.</p>
                  <p>Reply to this email if anything on it looks wrong.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  {{brandName}} has issued an updated {{documentList}} for booking
                  {{bookingReference}} ({{itinerarySummary}}).

                  It is attached to this email as a PDF, and it replaces the copy you were sent
                  before. Please use this one from now on.

                  Reply to this email if anything on it looks wrong.
                  """),

        // ------------------------------------------------------------- group departures (F6)
        //
        // The offer names its deadline in the agency's own words for a date, and asks the traveller
        // to reply rather than sending them to a link: there is no traveller account to log into,
        // and every URL we could print today would be on our own domain.
        TravellerFacing(
            DepartureWaitlistOffer,
            version: 1,
            subject: "A seat has come up on {{departureTitle}}",
            tokens: ["departureTitle", "departureDate", "paxCount", "offerExpiresAt"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>A seat has come up on <strong>{{departureTitle}}</strong>, leaving
                     {{departureDate}}, and you are next on the list.</p>
                  <table role="presentation" cellpadding="6" cellspacing="0">
                    <tr><td><strong>Travellers</strong></td><td>{{paxCount}}</td></tr>
                    <tr><td><strong>Held until</strong></td><td>{{offerExpiresAt}}</td></tr>
                  </table>
                  <p>Reply to this email to take it. After {{offerExpiresAt}} the seat goes to the
                     next person waiting.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  A seat has come up on {{departureTitle}}, leaving {{departureDate}}, and you are
                  next on the list.

                  Travellers:  {{paxCount}}
                  Held until:  {{offerExpiresAt}}

                  Reply to this email to take it. After {{offerExpiresAt}} the seat goes to the next
                  person waiting.
                  """),

        TravellerFacing(
            DepartureInstallmentReminder,
            version: 1,
            subject: "{{amountDue}} is due on {{dueDate}} — {{bookingReference}}",
            tokens: ["bookingReference", "departureTitle", "paymentLabel", "amountDue", "dueDate"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>Your next payment for <strong>{{departureTitle}}</strong> is due.</p>
                  <table role="presentation" cellpadding="6" cellspacing="0">
                    <tr><td><strong>Reference</strong></td><td>{{bookingReference}}</td></tr>
                    <tr><td><strong>Payment</strong></td><td>{{paymentLabel}}</td></tr>
                    <tr><td><strong>Amount</strong></td><td>{{amountDue}}</td></tr>
                    <tr><td><strong>Due</strong></td><td>{{dueDate}}</td></tr>
                  </table>
                  <p>Reply to this email to arrange it. Nothing is charged automatically.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  Your next payment for {{departureTitle}} is due.

                  Reference:  {{bookingReference}}
                  Payment:    {{paymentLabel}}
                  Amount:     {{amountDue}}
                  Due:        {{dueDate}}

                  Reply to this email to arrange it. Nothing is charged automatically.
                  """),

        TravellerFacing(
            DepartureCancelled,
            version: 1,
            subject: "{{departureTitle}} on {{departureDate}} has been cancelled",
            tokens: ["bookingReference", "departureTitle", "departureDate"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>{{brandName}} has had to cancel <strong>{{departureTitle}}</strong>, which was
                     leaving {{departureDate}}.</p>
                  <table role="presentation" cellpadding="6" cellspacing="0">
                    <tr><td><strong>Reference</strong></td><td>{{bookingReference}}</td></tr>
                  </table>
                  <p>Everything you have paid for it is being refunded in full. Reply to this email
                     if you would like help finding another date.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  {{brandName}} has had to cancel {{departureTitle}}, which was leaving
                  {{departureDate}}.

                  Reference:  {{bookingReference}}

                  Everything you have paid for it is being refunded in full. Reply to this email if
                  you would like help finding another date.
                  """),

        // Agency-facing: the agent is the one who chases an overdue installment. Decision 13 — a
        // booking is never cancelled automatically for it.
        AgencyFacing(
            DepartureInstallmentOverdue,
            version: 1,
            subject: "Overdue payment on {{bookingReference}} — {{amountDue}}",
            tokens: ["bookingReference", "departureTitle", "travellerName", "paymentLabel", "amountDue", "dueDate", "daysOverdue"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>A payment on one of your group departure bookings is {{daysOverdue}} days
                     overdue. Nothing has been cancelled — it is yours to chase.</p>
                  <table role="presentation" cellpadding="6" cellspacing="0">
                    <tr><td><strong>Reference</strong></td><td>{{bookingReference}}</td></tr>
                    <tr><td><strong>Departure</strong></td><td>{{departureTitle}}</td></tr>
                    <tr><td><strong>Traveller</strong></td><td>{{travellerName}}</td></tr>
                    <tr><td><strong>Payment</strong></td><td>{{paymentLabel}}</td></tr>
                    <tr><td><strong>Amount</strong></td><td>{{amountDue}}</td></tr>
                    <tr><td><strong>Was due</strong></td><td>{{dueDate}}</td></tr>
                  </table>
                  <p>Suggested action: call the traveller, and either take the payment or release
                     the seat so somebody on the waitlist can have it.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  A payment on one of your group departure bookings is {{daysOverdue}} days overdue.
                  Nothing has been cancelled - it is yours to chase.

                  Reference:   {{bookingReference}}
                  Departure:   {{departureTitle}}
                  Traveller:   {{travellerName}}
                  Payment:     {{paymentLabel}}
                  Amount:      {{amountDue}}
                  Was due:     {{dueDate}}

                  Suggested action: call the traveller, and either take the payment or release the
                  seat so somebody on the waitlist can have it.
                  """),

        // The traveller's own way back into their booking. It goes to the agency's own site, and the
        // email says the agency's name: a traveller has no idea a platform is involved, and this is
        // one of the places it would be easiest to give that away (CLAUDE.md rule 4).
        TravellerFacing(
            BookingManageLink,
            version: 1,
            subject: "Manage your booking with {{brandName}} — {{bookingReference}}",
            tokens: ["bookingReference", "manageUrl"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>Thank you — your payment has gone through, and we are confirming the details now.</p>
                  <table role="presentation" cellpadding="6" cellspacing="0">
                    <tr><td><strong>Reference</strong></td><td>{{bookingReference}}</td></tr>
                  </table>
                  <p>You can see your booking, and download your documents as they are ready, here:</p>
                  <p><a href="{{manageUrl}}">Manage your booking</a></p>
                  <p>Keep this link — it is how you get back in, and there is no password to remember.
                     Reply to this email if anything looks wrong.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  Thank you - your payment has gone through, and we are confirming the details now.

                      Reference: {{bookingReference}}

                  You can see your booking, and download your documents as they are ready, here:

                      {{manageUrl}}

                  Keep this link - it is how you get back in, and there is no password to remember.
                  Reply to this email if anything looks wrong.
                  """),

        TravellerFacing(
            CrmQuoteSent,
            version: 1,
            subject: "Your quote from {{brandName}} — {{quoteNumber}}",
            tokens: ["quoteNumber", "quoteTitle", "quoteTotal", "validUntil", "quoteUrl"],
            html: """
                  <p>Hello {{recipientName}},</p>
                  <p>Here is your quote for {{quoteTitle}}.</p>
                  <table role="presentation" cellpadding="6" cellspacing="0">
                    <tr><td><strong>Quote</strong></td><td>{{quoteNumber}}</td></tr>
                    <tr><td><strong>Total</strong></td><td>{{quoteTotal}}</td></tr>
                    <tr><td><strong>Valid until</strong></td><td>{{validUntil}}</td></tr>
                  </table>
                  <p>Open it to see the full itinerary, and to accept or decline it:</p>
                  <p><a href="{{quoteUrl}}">View your quote</a></p>
                  <p>Reply to this email if you would like anything changed.</p>
                  """,
            text: """
                  Hello {{recipientName}},

                  Here is your quote for {{quoteTitle}}.

                      Quote:       {{quoteNumber}}
                      Total:       {{quoteTotal}}
                      Valid until: {{validUntil}}

                  Open it to see the full itinerary, and to accept or decline it:

                      {{quoteUrl}}

                  Reply to this email if you would like anything changed.
                  """),
    ];

    /// <summary>Finds the newest version of a template by key and channel, or null when this build has none.</summary>
    public static NotificationTemplateDefinition? Find(string key, NotificationChannel channel) =>
        All.Where(definition => definition.Key == key && definition.Channel == channel)
            .MaxBy(definition => definition.Version);

    /// <summary>The row the seeder stores for <paramref name="definition"/>, and what tests render from.</summary>
    public static NotificationTemplate ToTemplate(this NotificationTemplateDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return NotificationTemplate.Create(
            definition.Key,
            definition.Channel,
            definition.Locale,
            definition.Version,
            definition.Audience,
            definition.Subject,
            definition.Html,
            definition.Text);
    }

    /// <summary>
    /// Wraps agency-facing content in the house layout: no colours, no images, no web fonts.
    /// </summary>
    /// <remarks>
    /// Deliberately plain. The recipient is a travel business using our console, so the mail is
    /// ours to brand — and the most reliable brand in email is one that renders identically in
    /// Outlook 2016, Gmail with images blocked and a screen reader.
    /// </remarks>
    private static NotificationTemplateDefinition AgencyFacing(
        string key,
        int version,
        string subject,
        IReadOnlyList<string> tokens,
        string html,
        string text) =>
        new(
            key,
            NotificationChannel.Email,
            NotificationTemplate.DefaultLocale,
            version,
            NotificationAudience.AgencyStaff,
            subject,
            $"""
             <!doctype html>
             <html lang="en">
               <body>
             {Indent(html, "    ")}
               </body>
             </html>
             """,
            text,
            tokens);

    /// <summary>
    /// Wraps traveller-facing content in the <i>agency's</i> brand: their name, logo, colour and
    /// contact details, and no mention of ours.
    /// </summary>
    /// <remarks>
    /// The colour and logo arrive as tokens the renderer fills from <c>agency_branding</c>, so a
    /// template contains no literal colour of its own. The logo block is emitted only when the
    /// agency has uploaded one — <c>{{brandLogoUrl}}</c> is empty otherwise, and an
    /// <c>&lt;img src=""&gt;</c> renders as a broken image in several clients, so the renderer
    /// drops the whole block instead (see <c>NotificationRenderer</c>).
    /// </remarks>
    private static NotificationTemplateDefinition TravellerFacing(
        string key,
        int version,
        string subject,
        IReadOnlyList<string> tokens,
        string html,
        string text) =>
        new(
            key,
            NotificationChannel.Email,
            NotificationTemplate.DefaultLocale,
            version,
            NotificationAudience.Traveller,
            subject,
            $$$"""
             <!doctype html>
             <html lang="en">
               <body style="margin:0;padding:0">
                 <div style="background:{{brandColor}};padding:20px;color:#FFFFFF;font-size:20px">
                   {{{NotificationRenderer.LogoBlockOpen}}}<img src="{{brandLogoUrl}}" alt="{{brandName}}"
                        height="40" style="display:block;border:0">{{{NotificationRenderer.LogoBlockClose}}}
                   {{{NotificationRenderer.NameBlockOpen}}}{{brandName}}{{{NotificationRenderer.NameBlockClose}}}
                 </div>
                 <div style="padding:20px">
             {{{Indent(html, "    ")}}}
                 </div>
                 <div style="padding:20px;font-size:12px;color:#666666">
                   <p>{{brandName}}</p>
                   <p>{{brandContact}}</p>
                 </div>
               </body>
             </html>
             """,
            $$$"""
             {{{text}}}

             --
             {{brandName}}
             {{brandContact}}
             """,
            tokens);

    private static string Indent(string block, string indent) =>
        string.Join(
            '\n',
            block.Split('\n').Select(line => line.Length == 0 ? line : indent + line));
}
