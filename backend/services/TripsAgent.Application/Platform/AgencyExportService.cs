using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Application.Platform;

/// <summary>
/// Everything one agency owns, as one JSON document.
/// </summary>
/// <remarks>
/// <para>
/// FRD §2.15 asks for an export when an agency is terminated: they are entitled to their own
/// records, and we are about to stop being the place they live. JSON rather than a zip of CSVs
/// for the MVP — one file, one request, and the nesting is the relationship, which a flat CSV
/// would lose.
/// </para>
/// <para>
/// <b>It contains personal data.</b> Traveller names and contact details are in it, so the
/// endpoint above requires <c>agency.export</c>, which only a Super Admin holds, and every export
/// writes an audit entry naming who took it.
/// </para>
/// <para>
/// What is deliberately not in it: password hashes, tokens and supplier credentials. An export is
/// a record of the business, not a way to walk off with the keys to it.
/// </para>
/// </remarks>
public sealed class AgencyExportService
{
    /// <summary>Pretty-printed: somebody opens this in a text editor, not a parser.</summary>
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,

        // camelCase, matching every other response the platform sends, so an agency reading their
        // export next to an API response is not reading two different spellings of one field.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly IAuditContext _audit;
    private readonly TimeProvider _clock;

    public AgencyExportService(
        IAppDbContext db,
        IPlatformScope platformScope,
        IAuditContext audit,
        TimeProvider clock)
    {
        _db = db;
        _platformScope = platformScope;
        _audit = audit;
        _clock = clock;
    }

    /// <summary>The file name the browser should save it as.</summary>
    public static string FileNameFor(string slug, DateTimeOffset at) =>
        $"trips-export-{slug}-{at:yyyyMMdd-HHmmss}.json";

    /// <summary>
    /// Builds the export, or returns null when there is no such agency.
    /// </summary>
    /// <remarks>
    /// Materialised into a string rather than streamed. An agency's whole history is measured in
    /// megabytes at the size this platform is, and a string is something the endpoint can hash,
    /// log the length of, and hand to the browser in one piece.
    /// </remarks>
    public async Task<AgencyExport?> BuildAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "Admin console agency export — reads one agency's whole record so it can be handed back to them");

        var agency = await _db.Agencies.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == agencyId, cancellationToken);

        if (agency is null)
        {
            return null;
        }

        var at = _clock.GetUtcNow();

        var users = await _db.Users.AsNoTracking()
            .Where(user => user.AgencyId == agencyId)
            .OrderBy(user => user.CreatedAt)
            .Select(user => new
            {
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.PhoneNumber,
                Status = user.Status.ToString(),
                user.EmailVerifiedAt,
                user.LastLoginAt,
                user.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var orders = await _db.Orders.AsNoTracking()
            .Where(order => order.AgencyId == agencyId)
            .OrderBy(order => order.CreatedAt)
            .Select(order => new
            {
                order.Id,
                order.OrderNumber,
                Status = order.Status.ToString(),
                Channel = order.Channel.ToString(),
                BuyerType = order.BuyerType.ToString(),
                order.Currency,
                order.TotalNetMinor,
                order.TotalMarkupMinor,
                order.TotalTaxMinor,
                order.TotalGrossMinor,
                order.TotalPlatformFeeMinor,
                order.PlacedAt,
                order.PaidAt,
                order.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var lines = await _db.OrderLines.AsNoTracking()
            .Where(line => line.AgencyId == agencyId)
            .OrderBy(line => line.CreatedAt)
            .Select(line => new
            {
                line.Id,
                line.OrderId,
                ItemType = line.ItemType.ToString(),
                line.TitleSnapshot,
                FulfilmentStatus = line.FulfilmentStatus.ToString(),
                line.NetAmountMinor,
                line.MarkupAmountMinor,
                line.TaxAmountMinor,
                line.GrossAmountMinor,
                line.Currency,
                line.SupplierBookingId,
                line.PlacedAt,
            })
            .ToListAsync(cancellationToken);

        var wallet = await _db.Wallets.AsNoTracking()
            .Where(candidate => candidate.AgencyId == agencyId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Currency,
                candidate.BalanceMinor,
                candidate.ReservedMinor,
                Status = candidate.Status.ToString(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        var walletTransactions = await _db.WalletTransactions.AsNoTracking()
            .Where(transaction => transaction.AgencyId == agencyId)
            .OrderBy(transaction => transaction.CreatedAt)
            .Select(transaction => new
            {
                transaction.Id,
                Type = transaction.Type.ToString(),
                transaction.AmountMinor,
                transaction.BalanceAfterMinor,
                transaction.Description,
                transaction.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var payload = new
        {
            exportedAt = at,
            exportedByUserId = _audit.ActorUserId,
            schema = "trips.agency-export.v1",
            agency = new
            {
                agency.Id,
                agency.LegalName,
                agency.TradingName,
                agency.Slug,
                Status = agency.Status.ToString(),
                Type = agency.Type.ToString(),
                agency.CountryCode,
                agency.BaseCurrency,
                agency.Timezone,
                agency.TaxId,
                agency.VatRateBasisPoints,
                agency.ParentAgencyId,
                agency.CreatedAt,
                agency.VerifiedAt,
                agency.StatusChangedAt,
                agency.StatusReason,
            },
            users,
            wallet,
            walletTransactions,
            orders,
            orderLines = lines,
        };

        var json = JsonSerializer.Serialize(payload, Format);

        // The export itself is an event worth recording: it is the single broadest read of one
        // agency's data the platform allows, and "who took a copy, and when" is exactly the
        // question somebody asks afterwards.
        _audit.SetReason($"{AgencyLifecycleService.Actions.Exported}: {agency.Slug}");

        return new AgencyExport(FileNameFor(agency.Slug, at), json, at);
    }
}

/// <summary>One agency's export, ready to hand to a browser.</summary>
/// <param name="FileName">What to save it as.</param>
/// <param name="Json">The document itself.</param>
/// <param name="GeneratedAt">When it was taken.</param>
public sealed record AgencyExport(string FileName, string Json, DateTimeOffset GeneratedAt);
