using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Commerce;

namespace TripsAgent.Infrastructure.Commerce;

/// <summary>
/// Reads the timings of the traveller's buying flow (build plan F5) from configuration.
/// </summary>
/// <remarks>
/// Bound by hand rather than through the options binder, for the same reason the pricing options
/// are: a typo in a production setting should stop startup loudly rather than fall back to a
/// default nobody chose. Every key is optional, and the defaults on
/// <see cref="CommerceOptions"/> are the ones the build plan describes.
/// </remarks>
public static class CommerceRegistration
{
    public static IServiceCollection AddCommerce(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(CommerceOptions.SectionName);

        var options = new CommerceOptions();

        if (ReadMinutes(section, "CartLifetimeMinutes") is { } cartLifetime)
        {
            options.CartLifetime = cartLifetime;
        }

        if (ReadMinutes(section, "PaymentWindowMinutes") is { } paymentWindow)
        {
            options.PaymentWindow = paymentWindow;
        }

        if (ReadMinutes(section, "BookingLinkLifetimeMinutes") is { } linkLifetime)
        {
            options.BookingLinkLifetime = linkLifetime;
        }

        if (ReadCount(section, "MaxCartItems") is { } maxItems)
        {
            options.MaxCartItems = maxItems;
        }

        if (ReadCount(section, "MaxPaxPerItem") is { } maxPax)
        {
            options.MaxPaxPerItem = maxPax;
        }

        services.AddSingleton(options);

        // Where the gateway returns a traveller to after the hosted payment page. Unset means the
        // agency's own site decides, which is the normal case; see StorefrontCheckoutService.
        services.AddSingleton(new CheckoutReturnUrl(section["ReturnUrl"]));

        return services;
    }

    private static TimeSpan? ReadMinutes(IConfiguration section, string key) =>
        ReadCount(section, key) is { } minutes ? TimeSpan.FromMinutes(minutes) : null;

    private static int? ReadCount(IConfiguration section, string key)
    {
        var raw = section[key];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new InvalidOperationException(
                $"{CommerceOptions.SectionName}:{key} must be a whole number of at least 1, and is '{raw}'.");
        }

        return value;
    }
}
