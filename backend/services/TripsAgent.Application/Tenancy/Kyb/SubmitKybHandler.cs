using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Platform;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.Kyb;

namespace TripsAgent.Application.Tenancy.Kyb;

/// <summary>What happened when an agency tried to submit.</summary>
public abstract record SubmitKybOutcome
{
    private SubmitKybOutcome()
    {
    }

    public sealed record Submitted(KybSubmission Submission) : SubmitKybOutcome;

    /// <summary>Required documents are still missing, named so the screen can highlight them.</summary>
    public sealed record Incomplete(IReadOnlyList<string> MissingDocumentTypes) : SubmitKybOutcome;

    /// <summary>Nothing to submit, or it is already with Trips.</summary>
    public sealed record NothingToSubmit : SubmitKybOutcome;
}

/// <summary>
/// Hands an agency's documents to Trips for review.
/// </summary>
/// <remarks>
/// Moves the agency to <c>pending_verification</c> and raises an <see cref="AdminAlert"/> in the
/// same transaction as the submission itself. If the alert were written separately, a failure
/// between the two would leave an agency waiting on a queue that never learned about it.
/// </remarks>
public sealed class SubmitKybHandler
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;

    public SubmitKybHandler(IAppDbContext db, ITenantContext tenant, TimeProvider clock)
    {
        _db = db;
        _tenant = tenant;
        _clock = clock;
    }

    public async Task<SubmitKybOutcome> HandleAsync(CancellationToken cancellationToken = default)
    {
        if (_tenant.AgencyId is not { } agencyId)
        {
            throw new InvalidOperationException("Submitting KYB needs a resolved tenant; this endpoint requires authentication.");
        }

        var submission = await _db.KybSubmissions
            .Where(s => s.AgencyId == agencyId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (submission is null || !submission.IsEditable)
        {
            return new SubmitKybOutcome.NothingToSubmit();
        }

        var provided = await _db.KybDocuments
            .Where(d => d.SubmissionId == submission.Id)
            .Select(d => d.DocumentType)
            .Distinct()
            .ToListAsync(cancellationToken);

        var missing = KybSubmission.RequiredDocuments
            .Where(required => !provided.Contains(required))
            .Select(required => required.ToString())
            .ToList();

        if (missing.Count > 0)
        {
            return new SubmitKybOutcome.Incomplete(missing);
        }

        var now = _clock.GetUtcNow();

        submission.Submit(provided, now);

        var agency = await _db.Agencies.FirstAsync(a => a.Id == agencyId, cancellationToken);

        // A resubmission after rejection puts the agency back into review rather than leaving it
        // showing as rejected while Trips looks at the corrected documents.
        agency.MarkPendingVerification();

        _db.AdminAlerts.Add(AdminAlert.ForPendingKyb(
            agencyId,
            submission.Id,
            agency.TradingName ?? agency.LegalName));

        // One SaveChanges: the submission, the agency's status and the alert commit together.
        await _db.SaveChangesAsync(cancellationToken);

        return new SubmitKybOutcome.Submitted(submission);
    }
}
