using System.Globalization;
using TripsAgent.Application.Notifications;

namespace TripsAgent.Application.Identity.Registration;

/// <summary>Renders the email that carries a verification code.</summary>
/// <remarks>
/// <para>
/// This one is correctly branded as Trips Agent. CLAUDE.md rule 4 keeps our brand off anything a
/// <i>traveller</i> sees; the recipient here is a travel business signing up with us, so it is our
/// own customer relationship. Traveller-facing email renders from the agency's branding record.
/// </para>
/// <para>
/// The wording is the <c>identity.verify-email</c> template in <see cref="NotificationTemplateCatalog"/>.
/// It is rendered here and sent at once rather than queued, because a queued notification stores its
/// variables and this one's variable is a live code — see <see cref="SynchronousEmail"/>.
/// </para>
/// </remarks>
public static class VerificationEmail
{
    public const string ProductName = NotificationTemplateCatalog.ProductName;

    public static EmailMessage Create(string to, string firstName, string code, TimeSpan validFor) =>
        SynchronousEmail.Render(
            NotificationTemplateCatalog.IdentityVerifyEmail,
            to,
            firstName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = code,
                ["minutes"] = ((int)validFor.TotalMinutes).ToString(CultureInfo.InvariantCulture),
            });
}
