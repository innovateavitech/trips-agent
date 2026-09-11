using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TripsAgent.Integrations.TripsAfrica;

/// <summary>
/// The integrity hash Trips Africa puts on every price confirmation, and the bus bearer token.
/// </summary>
/// <remarks>
/// <para>
/// <b>Computed here, on the server, and nowhere else.</b> The rule is
/// <c>SHA512("{MerchantKey}*{ConfirmationCode}*{NewPriceWhole}")</c> as lower-case hex. It proves a
/// confirmed price came from the supplier and was not edited on the way — which only holds while the
/// merchant key is secret. A browser that could compute it could forge it.
/// </para>
/// <para>
/// The adapter only computes the <i>expected</i> value. Comparing it with what the supplier sent is
/// <c>SupplierBooking.RecordPriceConfirmation</c>'s job, element by element, so a domestic or
/// round-trip confirmation — an array — cannot pass on the strength of its first element (#35).
/// </para>
/// </remarks>
public static class ConfirmationHash
{
    /// <summary>The expected hash for one confirmation element.</summary>
    /// <param name="newPriceWhole">The supplier's <c>NewPriceWhole</c>: whole naira, as it sent them.</param>
    public static string Compute(string merchantKey, string confirmationCode, long newPriceWhole)
    {
        ArgumentException.ThrowIfNullOrEmpty(merchantKey);
        ArgumentException.ThrowIfNullOrEmpty(confirmationCode);

        var input = string.Create(
            CultureInfo.InvariantCulture,
            $"{merchantKey}*{confirmationCode}*{newPriceWhole}");

        return Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(input)));
    }

    /// <summary>
    /// The bus API's bearer token: <c>SHA512("{MerchantKey}:{MerchantCode}")</c>.
    /// </summary>
    /// <remarks>
    /// Lower-case hex, the same encoding the documentation shows for the confirmation hash; the bus
    /// page does not name one. If staging refuses it, this is the line to look at.
    /// </remarks>
    public static string BusBearerToken(string merchantKey, string merchantCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(merchantKey);
        ArgumentException.ThrowIfNullOrEmpty(merchantCode);

        return Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes($"{merchantKey}:{merchantCode}")));
    }

}
