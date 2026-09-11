using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Tenancy;
using TripsAgent.Domain.Platform;
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
    private readonly IEmailSender _email;
    private readonly KybDocumentLink _links;
    private readonly TimeProvider _clock;
    private readonly ILogger<KybReviewHandler> _logger;

    public KybReviewHandler(
        IAppDbContext db,
        IPlatformScope platformScope,
        IAuditContext audit,
        IEmailSender email,
        KybDocumentLink links,
        TimeProvider clock,
        ILogger<KybReviewHandler> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _audit = audit;
        _email = email;
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

        await ResolveAlertsFor(submissionId, now, cancellationToken);

        _audit.SetReason("KYB approved");
        await _db.SaveChangesAsync(cancellationToken);

        await NotifyAsync(
            agency.Id,
            business => KybDecisionEmail.Approved(business.Email, business.Name),
            cancellationToken);

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

        // The reason is recorded against the audit row as well as shown to the agency, so the
        // log answers "why was this refused?" without going back to the submission.
        _audit.SetReason(reason);
        await _db.SaveChangesAsync(cancellationToken);

        await NotifyAsync(
            agency.Id,
            business => KybDecisionEmail.Rejected(business.Email, business.Name, reason),
            cancellationToken);

        return new KybDecisionOutcome.Decided();
    }

    /// <summary>The stored document behind a link, once the link has been checked.</summary>
    public async Task<KybDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter("KYB review — serves a document to a reviewer");

        return await _db.KybDocuments.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
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
    /// Emails the agency's owner about the decision.
    /// </summary>
    /// <remarks>
    /// After the decision is committed, and failures are logged rather than thrown: the decision
    /// is made and an admin should not see an error — or worse, retry and double-decide — because
    /// an SMTP server was briefly unavailable.
    /// </remarks>
    private async Task NotifyAsync(
        Guid agencyId,
        Func<(string Email, string Name), EmailMessage> compose,
        CancellationToken cancellationToken)
    {
        var recipient = await (
            from user in _db.Users
            join agency in _db.Agencies on user.AgencyId equals agency.Id
            where user.AgencyId == agencyId
            orderby user.CreatedAt
            select new { user.Email, Name = agency.TradingName ?? agency.LegalName })
            .FirstOrDefaultAsync(cancellationToken);

        if (recipient is null)
        {
            LogNoRecipient(_logger, agencyId);
            return;
        }

        try
        {
            await _email.SendAsync(compose((recipient.Email, recipient.Name)), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNotifyFailed(_logger, ex, agencyId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agency {AgencyId} has no user to notify about its KYB decision.")]
    private static partial void LogNoRecipient(ILogger logger, Guid agencyId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Could not email agency {AgencyId} about its KYB decision. The decision itself is saved.")]
    private static partial void LogNotifyFailed(ILogger logger, Exception exception, Guid agencyId);
}
