using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Notifications;

/// <summary>How a notification reaches someone.</summary>
/// <remarks>
/// Only <see cref="Email"/> is dispatched in M1 — it is the one channel we already have a provider
/// for. The other two are named here rather than later because a template is keyed by channel, and
/// adding a value to this enum after templates exist means a data migration.
/// </remarks>
public enum NotificationChannel
{
    Email = 1,

    /// <summary>Arrives with the SMS provider decision; `notifications.sms` is already a queue.</summary>
    Sms = 2,

    /// <summary>A bell in the console. No provider, so nothing can fail to deliver.</summary>
    InApp = 3,
}

/// <summary>Who a template is written for, which decides whose brand wraps it.</summary>
/// <remarks>
/// This is the enum CLAUDE.md rule 4 turns on. <see cref="AgencyStaff"/> is our own customer
/// relationship, so those emails say Trips Agent; <see cref="Traveller"/> is the agent's customer,
/// who must never learn we exist — their mail carries the agency's name and colours, from
/// <c>agency_branding</c>.
/// </remarks>
public enum NotificationAudience
{
    /// <summary>A person at the travel agency. Trips-branded.</summary>
    AgencyStaff = 1,

    /// <summary>The agency's own customer. Agency-branded, always.</summary>
    Traveller = 2,
}

/// <summary>
/// One rendering of one message, for one channel, in one locale, at one version.
/// </summary>
/// <remarks>
/// <para>
/// Rows are seeded from <c>NotificationTemplateCatalog</c> rather than typed into the database, so
/// wording changes arrive through a pull request like any other change. The table exists anyway
/// because a notification records the exact version it was rendered from — "which wording did this
/// customer actually receive in March" is a question support gets asked, and a template held only
/// in the current build cannot answer it.
/// </para>
/// <para>
/// <b>Versions are immutable.</b> The seeder inserts versions it does not find and never edits one
/// it does. Changing wording means a new version, which becomes the active one; the old row stays
/// behind so notifications that point at it still resolve.
/// </para>
/// <para>
/// Platform-owned, so no <c>agency_id</c> and no tenant filter: every agency's mail renders from
/// the same wording, with only the branding differing. Per-agency template overrides are a later
/// issue (plan §2.12) and will hang off this table.
/// </para>
/// </remarks>
public sealed class NotificationTemplate : Entity, IAuditableEntity
{
    /// <summary>The locale everything falls back to. Nigeria is the only market in M1.</summary>
    public const string DefaultLocale = "en-NG";

    private NotificationTemplate()
    {
        Key = string.Empty;
        Locale = DefaultLocale;
        SubjectTemplate = string.Empty;
        HtmlTemplate = string.Empty;
        TextTemplate = string.Empty;
    }

    /// <summary>Creates a version of a template. Called by the seeder, never by a request.</summary>
    /// <param name="key">Stable identifier, e.g. <c>kyb.approved</c>. Never renamed — notifications point at it.</param>
    /// <param name="channel">Which channel this rendering is for.</param>
    /// <param name="locale">BCP-47 tag, e.g. <c>en-NG</c>.</param>
    /// <param name="version">Bumped whenever the wording below changes.</param>
    /// <param name="audience">Whose brand wraps it. See <see cref="NotificationAudience"/>.</param>
    /// <param name="subjectTemplate">Subject line, with <c>{{token}}</c> placeholders.</param>
    /// <param name="htmlTemplate">HTML body. Token values are HTML-encoded when rendered.</param>
    /// <param name="textTemplate">Plain-text body. Required — see <c>EmailMessage.TextBody</c>.</param>
    public static NotificationTemplate Create(
        string key,
        NotificationChannel channel,
        string locale,
        int version,
        NotificationAudience audience,
        string subjectTemplate,
        string htmlTemplate,
        string textTemplate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectTemplate);
        ArgumentException.ThrowIfNullOrWhiteSpace(htmlTemplate);
        ArgumentException.ThrowIfNullOrWhiteSpace(textTemplate);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        return new NotificationTemplate
        {
            Key = key,
            Channel = channel,
            Locale = locale,
            Version = version,
            Audience = audience,
            SubjectTemplate = subjectTemplate,
            HtmlTemplate = htmlTemplate,
            TextTemplate = textTemplate,
        };
    }

    /// <summary>Stable identifier shared by every channel, locale and version of this message.</summary>
    public string Key { get; private set; }

    public NotificationChannel Channel { get; private set; }

    /// <summary>BCP-47 language tag. <see cref="DefaultLocale"/> is what an unknown locale falls back to.</summary>
    public string Locale { get; private set; }

    /// <summary>Which revision of the wording this is. Unique with key, channel and locale.</summary>
    public int Version { get; private set; }

    public NotificationAudience Audience { get; private set; }

    public string SubjectTemplate { get; private set; }

    public string HtmlTemplate { get; private set; }

    public string TextTemplate { get; private set; }

    /// <summary>When this version stopped being the one new notifications render from.</summary>
    /// <remarks>
    /// Set by the seeder when a newer version appears. Retired rather than deleted: a notification
    /// row keeps the version it rendered from, and a dangling version number answers nothing.
    /// </remarks>
    public DateTimeOffset? RetiredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True while new notifications may render from this version.</summary>
    public bool IsActive => RetiredAt is null;

    /// <summary>A newer version has taken over.</summary>
    public void Retire(DateTimeOffset at) => RetiredAt ??= at;
}
