using System.Globalization;
using System.Text.RegularExpressions;

namespace TripsAgent.Domain.Crm;

/// <summary>
/// How the CRM reads an email address or a phone number, so the same person is found again.
/// </summary>
/// <remarks>
/// <para>
/// Customers are never keyed in first (FRD §2.8 RS-1). Every inquiry, quote and booking looks its
/// customer up by email, then by phone, and creates one only when neither matches. That only works
/// if <c>Ada@Example.com</c> and <c>ada@example.com</c> — or <c>0803 000 1122</c> and
/// <c>+234 803 000 1122</c> — are recognised as one person. These are the rules that decide it.
/// </para>
/// <para>
/// Deliberately forgiving about how a number is written and strict about nothing else: an agent
/// keys in what the customer said, and a checker that rejects "0803-000-1122" only teaches people
/// to leave the field blank.
/// </para>
/// </remarks>
public static partial class ContactDetails
{
    /// <summary>The address trimmed and lower-cased, or null when blank. Stored like this, and matched like this.</summary>
    public static string? NormaliseEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    /// <summary>The number as written, trimmed, with runs of spaces made single. Null when blank.</summary>
    public static string? TidyPhone(string? phone) =>
        string.IsNullOrWhiteSpace(phone) ? null : WhitespaceRun().Replace(phone.Trim(), " ");

    /// <summary>
    /// The digits that identify a phone number, for matching. Null when there are none.
    /// </summary>
    /// <remarks>
    /// A Nigerian number written the international way is the same line as the local way, so
    /// <c>+234 803 000 1122</c>, <c>00234 803 000 1122</c> and <c>0803 000 1122</c> all become
    /// <c>08030001122</c>. Numbers from anywhere else keep their digits as written.
    /// </remarks>
    public static string? PhoneKey(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = string.Concat(phone.Where(char.IsAsciiDigit));

        if (digits.Length == 0)
        {
            return null;
        }

        if (digits.Length == 15 && digits.StartsWith("00234", StringComparison.Ordinal))
        {
            return string.Concat("0", digits.AsSpan(5));
        }

        if (digits.Length == 13 && digits.StartsWith("234", StringComparison.Ordinal))
        {
            return string.Concat("0", digits.AsSpan(3));
        }

        return digits;
    }

    /// <summary>True for something shaped like an email address: text, an @, a domain with a dot.</summary>
    public static bool IsEmail(string email) =>
        email.Length <= CrmLimits.MaxEmailLength && EmailShape().IsMatch(email);

    /// <summary>True for digits written the ways people write phone numbers, seven to fifteen of them.</summary>
    public static bool IsPhone(string phone) =>
        phone.Length <= CrmLimits.MaxPhoneLength
        && PhoneShape().IsMatch(phone)
        && PhoneKey(phone) is { Length: >= 7 and <= 15 };

    /// <summary>Every problem with a customer's name and contact details.</summary>
    /// <param name="prefix">
    /// What the fields are called in the request: <c>customer.</c> on a lead the agent keys in, empty
    /// on the storefront's trip-request form.
    /// </param>
    public static IReadOnlyList<CrmProblem> Check(string? name, string? email, string? phone, string prefix)
    {
        var problems = new List<CrmProblem>();

        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add(new($"{prefix}name", "Give the name of the person the trip is for."));
        }
        else if (name.Trim().Length > CrmLimits.MaxNameLength)
        {
            problems.Add(new(
                $"{prefix}name",
                string.Create(CultureInfo.InvariantCulture, $"Keep the name to {CrmLimits.MaxNameLength} characters.")));
        }

        var tidyEmail = NormaliseEmail(email);
        var tidyPhone = TidyPhone(phone);

        // One or the other: without either there is no way to answer the inquiry, and no way to
        // recognise the same person when they ask again.
        if (tidyEmail is null && tidyPhone is null)
        {
            problems.Add(new($"{prefix}email", "Give an email address or a phone number, so they can be reached."));
        }

        if (tidyEmail is not null && !IsEmail(tidyEmail))
        {
            problems.Add(new($"{prefix}email", "That does not look like an email address."));
        }

        if (tidyPhone is not null && !IsPhone(tidyPhone))
        {
            problems.Add(new($"{prefix}phone", "That does not look like a phone number."));
        }

        return problems;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailShape();

    // Digits, spaces and the punctuation people write numbers with: +234 (0) 803-000-1122.
    [GeneratedRegex(@"^\+?[0-9][0-9 ()\-.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneShape();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();
}
