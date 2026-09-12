namespace TripsAgent.Application.Documents;

/// <summary>
/// Issue an order's invoice and vouchers, render them and email them. Published through the
/// outbox; consumed from <c>documents.render</c>.
/// </summary>
/// <param name="AgencyId">
/// Whose order it is. The Worker acts as this agency, and only this agency, while it works — see
/// <see cref="OrderDocumentService"/>.
/// </param>
/// <param name="OrderId">The order.</param>
/// <param name="RecipientName">Who the documents are made out to.</param>
/// <param name="RecipientEmail">Where to email them, or null to email nothing.</param>
public sealed record OrderDocumentsRequested(Guid AgencyId, Guid OrderId, string RecipientName, string? RecipientEmail);

/// <summary>
/// Render one document that is already numbered — a reissue — and email it to its recipient.
/// Consumed from <c>documents.render</c>.
/// </summary>
public sealed record DocumentRenderRequested(Guid AgencyId, Guid DocumentId);
