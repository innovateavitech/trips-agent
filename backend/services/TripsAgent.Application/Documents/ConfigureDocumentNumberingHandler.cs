using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Documents;

namespace TripsAgent.Application.Documents;

/// <summary>What an agency wants its numbers for one document type to look like.</summary>
/// <param name="DocumentType">Which document's numbering to change.</param>
/// <param name="Prefix">Leads every number, e.g. <c>INV-LAGOS</c>. Upper-cased on save.</param>
/// <param name="Padding">How many digits the counter is padded to.</param>
/// <param name="IncludeYear">Show the issue year in the number.</param>
/// <param name="ResetsYearly">Start the counter again at 1 every year. Requires the year.</param>
public sealed record ConfigureDocumentNumberingCommand(
    DocumentType DocumentType,
    string Prefix,
    int Padding,
    bool IncludeYear,
    bool ResetsYearly);

/// <summary>How a numbering change went.</summary>
public abstract record ConfigureDocumentNumberingOutcome
{
    private ConfigureDocumentNumberingOutcome()
    {
    }

    /// <summary>Saved. Every document issued from now on uses it.</summary>
    public sealed record Configured(DocumentNumberFormat Format) : ConfigureDocumentNumberingOutcome;

    /// <summary>Refused, with a reason the agent can act on. Nothing was saved.</summary>
    public sealed record Invalid(string Reason) : ConfigureDocumentNumberingOutcome;
}

/// <summary>
/// Sets how the current agency's numbers for one document type are written.
/// </summary>
/// <remarks>
/// <para>
/// Only the look changes. The counter carries on from where it is — documents already issued keep
/// the numbers printed on them, and the next document takes the next value.
/// </para>
/// <para>
/// Turning yearly reset on or off switches which counter is drawn from (this year's, or the single
/// continuous one), so it can start a fresh count. The unique constraint on
/// <c>(agency_id, document_type, document_number)</c> is the backstop if a change would ever reprint
/// a number already issued: issuing fails loudly rather than duplicating.
/// </para>
/// </remarks>
public sealed class ConfigureDocumentNumberingHandler
{
    private readonly IAppDbContext _db;
    private readonly ITenantContext _tenant;

    public ConfigureDocumentNumberingHandler(IAppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<ConfigureDocumentNumberingOutcome> HandleAsync(
        ConfigureDocumentNumberingCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var agencyId = _tenant.AgencyId
            ?? throw new InvalidOperationException("Cannot configure document numbering with no agency resolved.");

        var problem = DocumentNumberFormat.FindProblem(
            command.Prefix, command.Padding, command.IncludeYear, command.ResetsYearly);

        if (problem is not null)
        {
            return new ConfigureDocumentNumberingOutcome.Invalid(problem);
        }

        // The tenant filter scopes this to the caller's agency, so there is one row or none.
        var format = await _db.DocumentNumberFormats
            .FirstOrDefaultAsync(f => f.DocumentType == command.DocumentType, cancellationToken);

        if (format is null)
        {
            format = DocumentNumberFormat.Configure(
                agencyId,
                command.DocumentType,
                command.Prefix,
                command.Padding,
                command.IncludeYear,
                command.ResetsYearly);

            _db.DocumentNumberFormats.Add(format);
        }
        else
        {
            format.Change(command.Prefix, command.Padding, command.IncludeYear, command.ResetsYearly);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new ConfigureDocumentNumberingOutcome.Configured(format);
    }
}
