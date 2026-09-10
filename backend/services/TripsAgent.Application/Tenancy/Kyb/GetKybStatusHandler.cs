using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Contracts.Tenancy;
using TripsAgent.Domain.Tenancy.Kyb;

namespace TripsAgent.Application.Tenancy.Kyb;

/// <summary>
/// Everything the onboarding screens need to render the KYB step in any of its states.
/// </summary>
/// <remarks>
/// One call rather than several, because the screen has to decide between "upload your
/// documents", "we are reviewing", "here is why it was rejected" and "you are verified" before it
/// renders anything — and a half-loaded version of that is worse than a spinner.
/// </remarks>
public sealed class GetKybStatusHandler
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;

    public GetKybStatusHandler(IAppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<KybStatusResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        if (_tenant.AgencyId is not { } agencyId)
        {
            throw new InvalidOperationException("Reading KYB status needs a resolved tenant; this endpoint requires authentication.");
        }

        var agency = await _db.Agencies.FirstAsync(a => a.Id == agencyId, cancellationToken);

        var submission = await _db.KybSubmissions
            .Where(s => s.AgencyId == agencyId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var documents = submission is null
            ? []
            : await _db.KybDocuments
                .Where(d => d.SubmissionId == submission.Id)
                .OrderBy(d => d.DocumentType)
                .Select(d => new KybDocumentResponse(
                    d.Id,
                    d.DocumentType.ToString(),
                    d.FileName,
                    d.ContentType,
                    d.SizeBytes,
                    d.CreatedAt))
                .ToListAsync(cancellationToken);

        var provided = documents.Select(d => d.DocumentType).ToHashSet(StringComparer.Ordinal);

        var missing = KybSubmission.RequiredDocuments
            .Select(required => required.ToString())
            .Where(required => !provided.Contains(required))
            .ToList();

        // The FRD asks for wallet funding to be visibly disabled with an explanation, not to fail
        // silently when pressed — so the reason travels with the flag.
        var funding = WalletFundingPolicy.For(agency);

        return new KybStatusResponse(
            SubmissionId: submission?.Id,
            Status: submission?.Status.ToString() ?? KybSubmissionStatus.Draft.ToString(),
            AgencyStatus: agency.Status.ToString(),
            SubmittedAt: submission?.SubmittedAt,
            ReviewedAt: submission?.ReviewedAt,
            RejectionReason: submission?.RejectionReason,
            CanEdit: submission?.IsEditable ?? true,
            Documents: documents,
            MissingDocumentTypes: missing,
            CanFundWallet: funding.IsAllowed,
            WalletFundingBlockedReason: funding.Reason);
    }
}
