using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Tenancy;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.Kyb;

namespace TripsAgent.Application.Tenancy.Kyb;

/// <summary>What happened to a review decision.</summary>
public abstract record KybDecisionOutcome
{
    private KybDecisionOutcome()
    {
    }

    public sealed record Decided : KybDecisionOutcome;

    /// <summary>No such submission, or it is not awaiting a decision.</summary>
    public sealed record NotAwaitingDecision : KybDecisionOutcome;

    /// <summary>A rejection arrived without a reason.</summary>
    public sealed record ReasonRequired : KybDecisionOutcome;
}

/// <summary>
/// The Trips-side of KYB: the queue, the documents, and the decision.
/// </summary>
/// <remarks>
/// <para>
/// Every method here reads or writes across agencies, which is exactly what
/// <see cref="IPlatformScope"/> exists for — entered explicitly, with a reason, and logged. The
/// endpoints are additionally gated on the <c>kyb.review</c> permission, so the scope is never
/// the only thing standing between an agent and another agency's documents.
/// </para>
/// <para>
/// Decisions are audited automatically: <c>KybSubmission</c> and <c>Agency</c> implement
/// <c>IAuditLogged</c>, so the save interceptor records actor, timestamp and before/after state
/// without this class calling anything.
/// </para>
/// </remarks>
public sealed partial class KybReviewHandler
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly IAuditContext _audit;
    private readonly INotifier _notifications;
    private readonly KybDocumentLink _links;
    private readonly TimeProvider _clock;
    private readonly ILogger<KybReviewHandler> _logger;

    public KybReviewHandler(
        IAppDbContext db,
        IPlatformScope platformScope,
        IAuditContext audit,
        INotifier notifications,
        KybDocumentLink links,
        TimeProvider clock,
        ILogger<KybReviewHandler> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _audit = audit;
        _notifications = notifications;
        _links = links;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Submissions waiting on Trips, oldest first.</summary>
    /// <remarks>
    /// Oldest first because an agency that cannot transact is losing business every day it waits,
    /// so the queue is a FIFO and not a stack.
    /// </remarks>
    public async Task<IReadOnlyList<KybQueueItemResponse>> QueueAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("KYB review queue — lists submissions from every agency");

        return await (
            from submission in _db.KybSubmissions
            join agency in _db.Agencies on submission.AgencyId equals agency.Id
            where submission.Status == KybSubmissionStatus.Submitted
                  || submission.Status == KybSubmissionStatus.UnderReview
            orderby submission.SubmittedAt
            select new KybQueueItemResponse(
                submission.Id,
                agency.Id,
                agency.TradingName ?? agency.LegalName,
                agency.CountryCode,
                submission.Status.ToString(),
                submission.SubmittedAt,
                _db.KybDocuments.Count(d => d.SubmissionId == submission.Id)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>One submission with its documents, each behind a time-limited link.</summary>
    public async Task<KybReviewDetailResponse?> DetailAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("KYB review — reads one agency's submission and documents");

        var submission = await _db.KybSubmissions
            .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);

        if (submission is null)
        {
            return null;
        }

        var agency = await _db.Agencies.FirstAsync(a => a.Id == submission.AgencyId, cancellationToken);

        var documents = await _db.KybDocuments
            .Where(d => d.SubmissionId == submissionId)
            .OrderBy(d => d.DocumentType)
            .ToListAsync(cancellationToken);

        var viewable = documents.Select(document =>
        {
            var link = _links.Create(document.Id);

            return new KybReviewDocumentResponse(
                document.Id,
                document.DocumentType.ToString(),
                document.FileName,
                document.ContentType,
                document.SizeBytes,
                link.Path,
                link.ExpiresAt);
        }).ToList();

        return new KybReviewDetailResponse(
            submission.Id,
            agency.Id,
            agency.LegalName,
            agency.TradingName,
            agency.CountryCode,
            agency.Status.ToString(),
            submission.Status.ToString(),
            submission.SubmittedAt,
            submission.RejectionReason,
            viewable);
    }

    /// <summary>Approves a submission and verifies the agency.</summary>
    public async Task<KybDecisionOutcome> ApproveAsync(
        Guid submissionId,
        Guid reviewerUserId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("KYB decision — approves an agency's submission");

        var submission = await _db.KybSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);

        if (submission is null || !submission.IsAwaitingDecision)
        {
            return new KybDecisionOutcome.NotAwaitingDecision();
        }

        var now = _clock.GetUtcNow();
        var agency = await _db.Agencies.FirstAsync(a => a.Id == submission.AgencyId, cancellationToken);

        submission.Approve(reviewerUserId, now);
        agency.MarkVerified(now);

        // In the same save as the approval. A verified agency may start a top-up at once, and
        // a payment with no wallet to land in is money taken and never credited.
        await OpenWalletAsync(agency, cancellationToken);

        await ResolveAlertsFor(submissionId, now, cancellationToken);

        await NotifyAsync(
            agency,
            NotificationTemplateCatalog.KybApproved,
            submissionId,
            new Dictionary<string, string>(StringComparer.Ordinal),
            cancellationToken);

        _audit.SetReason("KYB approved");
        await _db.SaveChangesAsync(cancellationToken);

        return new KybDecisionOutcome.Decided();
    }

    /// <summary>
    /// Rejects a submission with a reason the agency will read.
    /// </summary>
    public async Task<KybDecisionOutcome> RejectAsync(
        Guid submissionId,
        Guid reviewerUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new KybDecisionOutcome.ReasonRequired();
        }

        using var scope = _platformScope.Enter("KYB decision — rejects an agency's submission");

        var submission = await _db.KybSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);

        if (submission is null || !submission.IsAwaitingDecision)
        {
            return new KybDecisionOutcome.NotAwaitingDecision();
        }

        var now = _clock.GetUtcNow();
        var agency = await _db.Agencies.FirstAsync(a => a.Id == submission.AgencyId, cancellationToken);

        submission.Reject(reviewerUserId, reason, now);
        agency.MarkRejected();

        await ResolveAlertsFor(submissionId, now, cancellationToken);

        await NotifyAsync(
            agency,
            NotificationTemplateCatalog.KybRejected,
            submissionId,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason },
            cancellationToken);

        // The reason is recorded against the audit row as well as shown to the agency, so the
        // log answers "why was this refused?" without going back to the submission.
        _audit.SetReason(reason);
        await _db.SaveChangesAsync(cancellationToken);

        return new KybDecisionOutcome.Decided();
    }

    /// <summary>The stored document behind a link, once the link has been checked.</summary>
    public async Task<KybDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("KYB review — serves a document to a reviewer");

        return await _db.KybDocuments.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
    }

    /// <summary>
    /// Opens the agency's wallet in its base currency, and the ledger account behind it.
    /// </summary>
    /// <remarks>
    /// Checks first, because an agency can be approved again after a later resubmission, and the
    /// unique indexes on both tables would refuse a second of either.
    /// </remarks>
    private async Task OpenWalletAsync(Agency agency, CancellationToken cancellationToken)
    {
        var currency = agency.BaseCurrency.Trim().ToUpperInvariant();

        if (!await _db.Wallets.AnyAsync(w => w.AgencyId == agency.Id && w.Currency == currency, cancellationToken))
        {
            _db.Wallets.Add(Wallet.OpenFor(agency.Id, currency));
        }

        var hasAccount = await _db.LedgerAccounts.AnyAsync(
            a => a.AgencyId == agency.Id && a.AccountType == LedgerAccountType.AgencyWallet && a.Currency == currency,
            cancellationToken);

        if (!hasAccount)
        {
            _db.LedgerAccounts.Add(LedgerAccount.ForAgency(
                agency.Id, LedgerAccountType.AgencyWallet, currency, $"{LedgerAccountType.AgencyWallet} ({currency})"));
        }
    }

    /// <summary>Closes the queue entry, so a decided submission stops appearing as outstanding.</summary>
    private async Task ResolveAlertsFor(Guid submissionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var alerts = await _db.AdminAlerts
            .Where(alert => alert.EntityId == submissionId && alert.Status != AdminAlertStatus.Resolved)
            .ToListAsync(cancellationToken);

        foreach (var alert in alerts)
        {
            alert.Resolve(now);
        }
    }

    /// <summary>
    /// Queues the email telling the agency's owner about the decision.
    /// </summary>
    /// <remarks>
    /// Staged in the same save as the decision, so the two commit together: there is no agency
    /// verified without being told, and no email about a decision that rolled back. The Worker
    /// sends it, and retries it, without the admin who decided ever waiting on a mail server.
    /// Keyed on the submission, so a decision replayed for the same submission queues nothing new.
    /// </remarks>
    private async Task NotifyAsync(
        Agency agency,
        string templateKey,
        Guid submissionId,
        Dictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var owner = await _db.Users
            .Where(user => user.AgencyId == agency.Id)
            .OrderBy(user => user.CreatedAt)
            .Select(user => new { user.Id, user.Email, user.FirstName })
            .FirstOrDefaultAsync(cancellationToken);

        if (owner is null)
        {
            LogNoRecipient(_logger, agency.Id);
            return;
        }

        values["businessName"] = agency.TradingName ?? agency.LegalName;

        await _notifications.QueueEmailAsync(
            new EmailNotificationRequest(
                agency.Id,
                templateKey,
                owner.Email,
                owner.FirstName,
                values,
                DedupeKey: $"{templateKey}:{submissionId}",
                RecipientUserId: owner.Id),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agency {AgencyId} has no user to notify about its KYB decision.")]
    private static partial void LogNoRecipient(ILogger logger, Guid agencyId);
}
