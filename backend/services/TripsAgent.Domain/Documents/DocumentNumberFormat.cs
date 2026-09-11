using System.Globalization;
using System.Text.RegularExpressions;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Domain.Documents;

/// <summary>
/// How one agency writes the numbers of one type of document — <c>INV-LAGOST-2026-000042</c>.
/// </summary>
/// <remarks>
/// <para>
/// A number is <c>{Prefix}-{Year}-{Counter}</c>, or <c>{Prefix}-{Counter}</c> without the year.
/// The counter is left-padded with zeros to <see cref="Padding"/> digits.
/// </para>
/// <para>
/// An agency that has never configured a type gets <see cref="DefaultFor"/>: the invoice prefix
/// from its settings, six digits, the year shown, and a fresh counter every year. The row exists
/// only once the agency changes something.
/// </para>
/// <para>
/// This class decides what a number <i>looks like</i>. Which counter value comes next is decided
/// by the database, under a row lock — see <c>IDocumentNumberAllocator</c>.
/// </para>
/// </remarks>
public sealed partial class DocumentNumberFormat : Entity, IAuditableEntity, ITenantScoped
{
    /// <summary>Longest prefix allowed. Keeps a full number short enough to print on one line.</summary>
    public const int MaxPrefixLength = 20;

    /// <summary>Fewest counter digits. One means no padding at all.</summary>
    public const int MinPadding = 1;

    /// <summary>Most counter digits. Ten is already ten billion documents.</summary>
    public const int MaxPadding = 10;

    /// <summary>Counter digits for an agency that has not chosen.</summary>
    public const int DefaultPadding = 6;

    /// <summary>Whether the counter restarts each year for an agency that has not chosen.</summary>
    public const bool DefaultResetsYearly = true;

    /// <summary>
    /// The sequence year used when the counter never resets. Zero is not a real year, so it can
    /// never collide with one.
    /// </summary>
    public const int ContinuousSequenceYear = 0;

    private DocumentNumberFormat()
    {
        Prefix = string.Empty;
    }

    public Guid AgencyId { get; private set; }

    public DocumentType DocumentType { get; private set; }

    /// <summary>Upper-case letters, digits, <c>-</c> and <c>/</c>, e.g. <c>INV-LAGOS</c>.</summary>
    public string Prefix { get; private set; }

    /// <summary>How many digits the counter is padded to.</summary>
    public int Padding { get; private set; }

    /// <summary>Whether the issue year appears in the number.</summary>
    public bool IncludeYear { get; private set; }

    /// <summary>
    /// Whether the counter starts again at 1 each year. Requires <see cref="IncludeYear"/> —
    /// otherwise January's <c>INV-000001</c> would repeat last January's.
    /// </summary>
    public bool ResetsYearly { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Creates an agency's own format for one document type.</summary>
    /// <exception cref="ArgumentException">The format breaks a rule — see <see cref="FindProblem"/>.</exception>
    public static DocumentNumberFormat Configure(
        Guid agencyId,
        DocumentType documentType,
        string prefix,
        int padding,
        bool includeYear,
        bool resetsYearly)
    {
        if (agencyId == Guid.Empty)
        {
            throw new ArgumentException("A document number format belongs to an agency.", nameof(agencyId));
        }

        var format = new DocumentNumberFormat
        {
            AgencyId = agencyId,
            DocumentType = documentType,
        };

        format.Change(prefix, padding, includeYear, resetsYearly);

        return format;
    }

    /// <summary>
    /// The format an agency gets before it configures one: its invoice prefix for invoices, a
    /// type code plus its booking prefix for everything else, six digits, yearly reset.
    /// </summary>
    /// <remarks>Not saved. It is recomputed from the settings each time, so it follows them.</remarks>
    public static DocumentNumberFormat DefaultFor(AgencySettings settings, DocumentType documentType)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var prefix = documentType switch
        {
            DocumentType.Invoice => settings.InvoicePrefix,
            DocumentType.Voucher => $"VCH-{settings.BookingReferencePrefix}",
            DocumentType.Itinerary => $"ITN-{settings.BookingReferencePrefix}",
            DocumentType.Quote => $"QUO-{settings.BookingReferencePrefix}",
            _ => throw new ArgumentOutOfRangeException(nameof(documentType), documentType, "Unknown document type."),
        };

        // A slug with no letters or digits derives an empty booking prefix, which would leave a
        // dangling separator — "VCH-". Trim it rather than fail the invoice.
        return Configure(
            settings.AgencyId,
            documentType,
            prefix.Trim().TrimEnd('-', '/'),
            DefaultPadding,
            includeYear: true,
            resetsYearly: DefaultResetsYearly);
    }

    /// <summary>
    /// Says what is wrong with a proposed format, in words an agent can act on, or null if nothing.
    /// </summary>
    public static string? FindProblem(string? prefix, int padding, bool includeYear, bool resetsYearly)
    {
        var normalised = Normalise(prefix);

        if (normalised.Length == 0)
        {
            return "Enter a prefix, for example INV.";
        }

        if (normalised.Length > MaxPrefixLength)
        {
            return $"The prefix can be at most {MaxPrefixLength} characters.";
        }

        if (!PrefixPattern().IsMatch(normalised))
        {
            return "The prefix can only contain letters, digits, '-' and '/', and must start and end with a letter or digit.";
        }

        if (padding is < MinPadding or > MaxPadding)
        {
            return $"The number of digits must be between {MinPadding} and {MaxPadding}.";
        }

        if (resetsYearly && !includeYear)
        {
            // Without the year in the number, a counter that restarts in January reissues
            // numbers already on last year's invoices — two tax documents, one number.
            return "A numbering that restarts every year must show the year, or next year's numbers would repeat this year's.";
        }

        return null;
    }

    /// <summary>
    /// Says why the agency may not turn yearly reset on or off, or null if it may.
    /// </summary>
    /// <param name="currentlyResetsYearly">What the agency uses now — its own format, or the default.</param>
    /// <param name="resetsYearly">What it asked for.</param>
    /// <param name="hasIssuedDocuments">Whether it has issued any document of this type, ever.</param>
    /// <remarks>
    /// <para>
    /// Changing the prefix, the padding or whether the year is shown is always safe: the counter
    /// carries on, and two different counter values never print the same text. Yearly reset is
    /// different. It decides <i>which</i> counter is drawn from — this year's, or the continuous one
    /// — and the counter switched to starts again from its own count. Its next number can then print
    /// exactly like one already issued: <c>LTL-2026-000001</c> a second time. The database refuses
    /// the duplicate, but the counter rolls back with the refusal, so every later attempt draws the
    /// same number and fails the same way. The agency could issue nothing more of that type.
    /// </para>
    /// <para>
    /// So once a document of the type exists, the choice is final. Before the first one, it is free.
    /// </para>
    /// </remarks>
    public static string? FindResetChangeProblem(
        bool currentlyResetsYearly,
        bool resetsYearly,
        bool hasIssuedDocuments)
    {
        if (currentlyResetsYearly == resetsYearly || !hasIssuedDocuments)
        {
            return null;
        }

        return "Whether numbering restarts every year cannot be changed once documents of this type have been issued, "
            + "because the new count would reuse numbers already printed. You can still change the prefix, the "
            + "number of digits and whether the year is shown.";
    }

    /// <summary>
    /// The calendar year a document was issued in, in the agency's own time zone.
    /// </summary>
    /// <remarks>
    /// An invoice issued at 00:30 on 1 January in Lagos is 23:30 on 31 December in UTC. Its tax year
    /// is the one on the agency's wall clock, not the server's.
    /// </remarks>
    public static int LocalYear(DateTimeOffset issuedAt, string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        return TimeZoneInfo.ConvertTime(issuedAt, zone).Year;
    }

    /// <summary>Replaces the format. Numbers already issued keep the text they were issued with.</summary>
    /// <exception cref="ArgumentException">The format breaks a rule — see <see cref="FindProblem"/>.</exception>
    public void Change(string prefix, int padding, bool includeYear, bool resetsYearly)
    {
        var problem = FindProblem(prefix, padding, includeYear, resetsYearly);

        if (problem is not null)
        {
            throw new ArgumentException(problem);
        }

        Prefix = Normalise(prefix);
        Padding = padding;
        IncludeYear = includeYear;
        ResetsYearly = resetsYearly;
    }

    /// <summary>
    /// Which counter a document issued in <paramref name="localYear"/> draws from: that year's when
    /// the numbering resets yearly, the single continuous one otherwise.
    /// </summary>
    public int SequenceYearFor(int localYear) => ResetsYearly ? localYear : ContinuousSequenceYear;

    /// <summary>Writes out the number for counter value <paramref name="sequenceNumber"/>.</summary>
    /// <remarks>
    /// A counter wider than <see cref="Padding"/> is written in full, never cut down: truncating
    /// 1000000 to six digits would print 000000 and collide with a number already issued.
    /// </remarks>
    public string Render(long sequenceNumber, int localYear)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequenceNumber, 1L);

        var counter = sequenceNumber.ToString(CultureInfo.InvariantCulture).PadLeft(Padding, '0');

        return IncludeYear
            ? string.Create(CultureInfo.InvariantCulture, $"{Prefix}-{localYear:D4}-{counter}")
            : $"{Prefix}-{counter}";
    }

    private static string Normalise(string? prefix) =>
        (prefix ?? string.Empty).Trim().ToUpperInvariant();

    [GeneratedRegex("^[A-Z0-9](?:[A-Z0-9/-]*[A-Z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}
