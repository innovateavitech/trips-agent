using TripsAgent.Domain.Notifications;

namespace TripsAgent.Application.Notifications;

/// <summary>
/// Renders a catalog template straight into an email, for the few messages that must be sent at
/// once and never queued.
/// </summary>
/// <remarks>
/// <para>
/// A queued notification stores its variables so the Worker can render it later. For the
/// verification code and the password-reset link, the variable <i>is</i> the secret — a row holding
/// it would be a live code sitting in the database. So those two are rendered here and handed
/// straight to <see cref="IEmailSender"/>, and nothing about them is stored.
/// </para>
/// <para>
/// The wording still lives in <see cref="NotificationTemplateCatalog"/>, versioned and reviewed like
/// every other template, and renders through the same <see cref="NotificationRenderer"/> — so its
/// HTML-encoding and header rules apply here too.
/// </para>
/// </remarks>
public static class SynchronousEmail
{
    /// <summary>Renders the newest version of <paramref name="templateKey"/> for one agency user.</summary>
    /// <exception cref="InvalidOperationException">The catalog has no such template, or it is traveller-facing.</exception>
    public static EmailMessage Render(
        string templateKey,
        string to,
        string recipientName,
        IReadOnlyDictionary<string, string> values)
    {
        var definition = NotificationTemplateCatalog.Find(templateKey, NotificationChannel.Email)
            ?? throw new InvalidOperationException($"There is no email template '{templateKey}'.");

        // Only our own users receive mail this way. A traveller's mail carries their agency's
        // brand, and that is looked up per agency by the dispatcher, never assumed here.
        if (definition.Audience != NotificationAudience.AgencyStaff)
        {
            throw new InvalidOperationException(
                $"Template '{templateKey}' is traveller-facing. Queue it through INotifier so it carries the agency's brand.");
        }

        var rendered = NotificationRenderer.Render(
            definition.ToTemplate(), NotificationBrand.Platform, recipientName, values);

        return new EmailMessage(to, rendered.Subject, rendered.Html, rendered.Text);
    }
}
