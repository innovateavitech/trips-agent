namespace TripsAgent.Domain.Crm;

/// <summary>One reason something in the CRM cannot be saved.</summary>
/// <param name="Field">
/// The request field it concerns, named as the request names it: <c>destination</c>,
/// <c>customer.email</c>, <c>items[0].quantity</c>.
/// </param>
/// <param name="Message">Written for the person who has to fix it, saying what to do.</param>
public sealed record CrmProblem(string Field, string Message);

/// <summary>
/// The CRM's size limits. The database repeats the lengths on its columns, so a value that gets
/// past these checks is still refused rather than truncated.
/// </summary>
public static class CrmLimits
{
    public const int MaxNameLength = 200;

    /// <summary>The longest address the email standard allows.</summary>
    public const int MaxEmailLength = 254;

    public const int MaxPhoneLength = 30;

    public const int MaxDestinationLength = 200;

    public const int MaxMessageLength = 4000;

    /// <summary>Adults, and separately children, on one inquiry. A bigger group is a group departure.</summary>
    public const int MaxPartySize = 99;

    public const int MaxReasonLength = 500;

    public const int MaxQuoteTitleLength = 200;

    public const int MaxItemDescriptionLength = 300;

    public const int MaxQuantity = 999;

    public const int MaxItems = 100;

    public const int MaxDays = 60;

    public const int MaxDayTitleLength = 200;

    public const int MaxDayDescriptionLength = 2000;

    public const int MaxNotesLength = 4000;

    public const int MaxTaskTitleLength = 300;

    public const int MaxSummaryLength = 2000;

    /// <summary>
    /// ₦100 billion, in kobo: the most one price or budget may be. The same ceiling markup rules use.
    /// It keeps 999 of them, a hundred lines deep, far inside a <c>long</c>.
    /// </summary>
    public const long MaxAmountMinor = 10_000_000_000_000;
}
