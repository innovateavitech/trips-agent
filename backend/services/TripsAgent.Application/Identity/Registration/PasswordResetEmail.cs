using System.Globalization;
using TripsAgent.Application.Notifications;

namespace TripsAgent.Application.Identity.Registration;

/// <summary>The email carrying a password reset link.</summary>
/// <remarks>
/// The wording is the <c>identity.password-reset</c> template in <see cref="NotificationTemplateCatalog"/>,
/// rendered here and sent at once rather than queued: the link is a working credential until it is
/// used or expires, and a queued notification would store it. See <see cref="SynchronousEmail"/>.
/// </remarks>
public static class PasswordResetEmail
{
    public const string ProductName = NotificationTemplateCatalog.ProductName;

    public static EmailMessage Create(string to, string firstName, string resetUrl, TimeSpan validFor) =>
        SynchronousEmail.Render(
            NotificationTemplateCatalog.IdentityPasswordReset,
            to,
            firstName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resetUrl"] = resetUrl,
                ["minutes"] = ((int)validFor.TotalMinutes).ToString(CultureInfo.InvariantCulture),
            });
}
