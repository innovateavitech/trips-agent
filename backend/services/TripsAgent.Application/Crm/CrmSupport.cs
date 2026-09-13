using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;

namespace TripsAgent.Application.Crm;

/// <summary>Reads an enum the API receives as its name.</summary>
public static class EnumNames
{
    /// <summary>
    /// The value named <paramref name="value"/>, ignoring case, or null for anything else.
    /// </summary>
    /// <remarks>
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> alone also accepts "3", and
    /// "New,Won" — which it ORs together into a number nobody meant. Neither is a name.
    /// </remarks>
    public static TEnum? Parse<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains(',', StringComparison.Ordinal)
            || value.Trim().All(character => char.IsDigit(character) || character is '-' or '+'))
        {
            return null;
        }

        return Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;
    }
}

/// <summary>The secret in a quote's link.</summary>
public static class QuoteLinkTokens
{
    /// <summary>The length of every token: 32 bytes, base64url without padding.</summary>
    public const int Length = 43;

    /// <summary>256 random bits, URL-safe. Unguessable, and meaningless: it names nothing.</summary>
    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>True for something shaped like a token. Anything else is turned away before the database is asked.</summary>
    public static bool LooksValid(string? token) =>
        token is { Length: Length } && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

/// <summary>How CRM values read in an email.</summary>
public static class CrmFormat
{
    /// <summary>"₦3,600,000.00", or "USD 1,250.00" for any other currency. From whole minor units, never a decimal.</summary>
    public static string Money(long amountMinor, string currency)
    {
        var sign = amountMinor < 0 ? "-" : string.Empty;
        var whole = Math.Abs(amountMinor) / 100;
        var kobo = Math.Abs(amountMinor) % 100;
        var symbol = currency == "NGN" ? "₦" : currency + " ";

        return string.Create(CultureInfo.InvariantCulture, $"{sign}{symbol}{whole:N0}.{kobo:D2}");
    }

    /// <summary>"18 September 2026".</summary>
    public static string Day(DateOnly date) => date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>"Fri 12 Sep, 10:00", on the clock in <paramref name="timeZone"/>.</summary>
    public static string Moment(DateTimeOffset at, string timeZone) =>
        CrmContext.InZone(at, timeZone).ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>Saving CRM changes that create a customer.</summary>
internal static class CrmSaves
{
    /// <summary>
    /// Stages the work with <paramref name="stage"/> and saves it. When the save is refused because
    /// another request created the same customer a moment earlier — the unique email index — the
    /// context is cleared and the work staged and saved once more. The second attempt finds that
    /// customer instead of creating another.
    /// </summary>
    public static async Task<T> RetryOnceOnUniqueViolationAsync<T>(
        IAppDbContext db,
        IUniqueViolationDetector uniqueViolations,
        Func<Task<T>> stage,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await stage();

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return result;
            }
            catch (DbUpdateException ex) when (attempt == 1 && uniqueViolations.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
