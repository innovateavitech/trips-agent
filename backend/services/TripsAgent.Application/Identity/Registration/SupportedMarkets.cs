namespace TripsAgent.Application.Identity.Registration;

/// <summary>
/// The countries an agency can register from, and the defaults each implies.
/// </summary>
/// <remarks>
/// Nigeria only, for now. The platform is built for the Nigerian market, and the defaults below
/// feed straight into tax and currency, so a wrong guess for an unlisted country would be worse
/// than refusing it. The currency and timezone are defaults only: the onboarding wizard (#50) lets
/// the agency confirm or change them. Adding a market is a one-line change here — it is a product
/// decision, not a technical one.
/// </remarks>
public static class SupportedMarkets
{
    public static IReadOnlyDictionary<string, (string Currency, string Timezone)> ByCountry { get; } =
        new Dictionary<string, (string Currency, string Timezone)>(StringComparer.OrdinalIgnoreCase)
        {
            ["NG"] = ("NGN", "Africa/Lagos"),
        };
}
