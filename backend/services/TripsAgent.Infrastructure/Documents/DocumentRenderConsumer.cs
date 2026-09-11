using MassTransit;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Infrastructure.Documents;

/// <summary>A document render failed, and the broker should redeliver the message or dead-letter it.</summary>
public sealed class DocumentRenderException : Exception
{
    public DocumentRenderException(string message)
        : base(message)
    {
    }

    public DocumentRenderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DocumentRenderException()
        : base("The document could not be rendered.")
    {
    }
}

/// <summary>
/// Issues, renders and emails documents for the messages on <c>documents.render</c>. Bound in
/// <c>MessagingRegistration</c>.
/// </summary>
/// <remarks>
/// <para>
/// A thin adapter, like <c>NotificationQueuedConsumer</c>: <see cref="OrderDocumentService"/> holds
/// the logic and is tested against real PostgreSQL without a broker. This does two things only.
/// </para>
/// <para>
/// <b>It acts as the message's agency.</b> Before anything is read it sets the tenant from the
/// message, which was written in that agency's own transaction. From then on the tenant filter and
/// row-level security confine this delivery to that one agency's rows, exactly as they confine a
/// request — narrower than a platform scope, and what the numbering allocator requires.
/// </para>
/// <para>
/// <b>It translates the outcome</b> into the one thing MassTransit understands: throw to be retried,
/// return to be done with. The final failure throws too, after the row is marked failed, so the
/// message lands in <c>documents.render_error</c> where a person looking at the broker expects it.
/// </para>
/// </remarks>
public sealed class DocumentRenderConsumer(TenantContext tenant, OrderDocumentService documents)
    : IConsumer<OrderDocumentsRequested>, IConsumer<DocumentRenderRequested>
{
    public async Task Consume(ConsumeContext<OrderDocumentsRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Message;
        ActAs(request.AgencyId);

        Translate(
            await documents.IssueForOrderAsync(request, context.CancellationToken),
            $"Documents for order {request.OrderId}");
    }

    public async Task Consume(ConsumeContext<DocumentRenderRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Message;
        ActAs(request.AgencyId);

        Translate(
            await documents.RenderDocumentAsync(request, context.CancellationToken),
            $"Document {request.DocumentId}");
    }

    private void ActAs(Guid agencyId)
    {
        // A fresh scope per delivery, so normally unset. A retry that reuses the scope finds it
        // already set to the same agency, which is fine; set to any other agency, it is a bug.
        if (tenant.AgencyId is null)
        {
            tenant.SetTenant(agencyId);
        }
        else if (tenant.AgencyId != agencyId)
        {
            throw new InvalidOperationException(
                $"This delivery is already acting as agency {tenant.AgencyId}; it cannot switch to {agencyId}.");
        }
    }

    private static void Translate(DocumentRunOutcome outcome, string what)
    {
        switch (outcome)
        {
            case DocumentRunOutcome.RetryLater:
                throw new DocumentRenderException($"{what} failed to render and will be retried. The error is on the document.");

            case DocumentRunOutcome.GaveUp:
                throw new DocumentRenderException(
                    $"{what} has been marked failed after its last attempt. See last_render_error on the document.");

            default:
                return;
        }
    }
}
