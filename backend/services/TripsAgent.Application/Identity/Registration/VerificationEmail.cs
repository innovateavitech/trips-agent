using System.Globalization;
using TripsAgent.Application.Notifications;

namespace TripsAgent.Application.Identity.Registration;

/// <summary>Renders the email that carries a verification code.</summary>
/// <remarks>
/// <para>
/// Branded as Trips Agent for a travel business that signed up with us. CLAUDE.md rule 4 keeps our
/// brand off anything a <i>traveller</i> sees; the recipient here is our own customer. Traveller-facing
/// email renders from the agency's branding record.
/// </para>
/// <para>
/// The exception is the staff of a sub-agent, whose code carries their principal's brand — the one
/// the invitation they joined through carried (build-plan decision 6, issue 170).
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

    /// <summary>The message, in our brand — or in <paramref name="brand"/>, a sub-agent's principal's, when given.</summary>
    public static EmailMessage Create(
        string to,
        string firstName,
        string code,
        TimeSpan validFor,
        NotificationBrand? brand = null) =>
        SynchronousEmail.Render(
            NotificationTemplateCatalog.IdentityVerifyEmail,
            to,
            firstName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = code,
                ["minutes"] = ((int)validFor.TotalMinutes).ToString(CultureInfo.InvariantCulture),
            },
            brand);
}
