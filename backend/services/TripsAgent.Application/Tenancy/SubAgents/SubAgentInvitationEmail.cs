using System.Globalization;
using TripsAgent.Application.Notifications;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Notifications;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Tenancy.SubAgents;

/// <summary>
/// Where the console's "accept your invitation" page lives.
/// </summary>
/// <remarks>
/// The console owns that page, so its address is configuration — exactly like the password-reset
/// link. Registered in <c>Infrastructure.DependencyInjection</c> from <c>Console:SubAgentInviteUrl</c>.
/// </remarks>
public sealed class SubAgentInviteLinkBuilder
{
    private readonly string _baseUrl;

    public SubAgentInviteLinkBuilder(string baseUrl) =>
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "https://localhost:5173/accept-invitation"
            : baseUrl.TrimEnd('/');

    public string Build(string token) => $"{_baseUrl}?token={Uri.EscapeDataString(token)}";
}

/// <summary>
/// The email inviting a business to join a principal's network.
/// </summary>
/// <remarks>
/// <para>
/// <b>It carries the principal's name, never ours.</b> Build-plan decision 6: sub-agents sell
/// under their principal's brand, and this is the first thing the new agent ever sees from us. So
/// the brand handed to the renderer is built from the principal's <c>agency_branding</c> row and
/// its trading name, not <see cref="NotificationBrand.Platform"/>.
/// </para>
/// <para>
/// That is also why it does not go through <c>SynchronousEmail</c>, which forces our own brand on
/// everything it renders. It is still sent at once and never queued, for the usual reason: the
/// link is a working credential until it is used, and a queued row would store it.
/// </para>
/// </remarks>
public static class SubAgentInvitationEmail
{
    /// <summary>Builds the message. <paramref name="branding"/> may be null if the row is missing.</summary>
    public static EmailMessage Create(
        string to,
        string businessName,
        Agency principal,
        AgencyBranding? branding,
        string inviteUrl)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var definition = NotificationTemplateCatalog.Find(
            NotificationTemplateCatalog.SubAgentInvitation, NotificationChannel.Email)
            ?? throw new InvalidOperationException(
                $"There is no email template '{NotificationTemplateCatalog.SubAgentInvitation}'.");

        var brand = new NotificationBrand(
            principal.TradingName ?? principal.LegalName,
            branding?.PrimaryColor ?? AgencyBranding.DefaultPrimaryColor,
            LogoUrl: null,
            branding?.ContactAddress,
            ReplyTo: null);

        var rendered = NotificationRenderer.Render(
            definition.ToTemplate(),
            brand,
            businessName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["inviteUrl"] = inviteUrl,
                ["days"] = ((int)UserInvitation.Lifetime.TotalDays).ToString(CultureInfo.InvariantCulture),
            });

        // The display name is the principal's too, so the inbox line does not give us away.
        return new EmailMessage(to, rendered.Subject, rendered.Html, rendered.Text, FromName: brand.Name);
    }
}
