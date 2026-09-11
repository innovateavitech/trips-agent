using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Notifications;
using TripsAgent.Domain.Notifications;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.Infrastructure.Notifications;

/// <summary>
/// Copies <see cref="NotificationTemplateCatalog"/> into <c>notifications.notification_templates</c>.
/// Run by <c>migrate</c>, through <see cref="ReferenceDataSeeder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Inserts the versions it does not find, and never rewrites one it does: a notification records
/// the version it rendered from, and that row must still say what the customer actually received.
/// </para>
/// <para>
/// When a newer version of a template appears, the older ones are retired rather than deleted, for
/// the same reason. The dispatcher only renders from the active one.
/// </para>
/// </remarks>
public static class NotificationTemplateSeeder
{
    /// <summary>Ensures every catalog version exists and only the newest is active. Returns how many were added.</summary>
    public static async Task<int> EnsureAsync(
        AppDbContext dbContext,
        TimeProvider clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(clock);

        var existing = await dbContext.NotificationTemplates.ToListAsync(cancellationToken);
        var added = 0;

        foreach (var definition in NotificationTemplateCatalog.All)
        {
            var present = existing.Any(row =>
                row.Key == definition.Key
                && row.Channel == definition.Channel
                && row.Locale == definition.Locale
                && row.Version == definition.Version);

            if (present)
            {
                continue;
            }

            var template = definition.ToTemplate();
            dbContext.NotificationTemplates.Add(template);
            existing.Add(template);
            added++;
        }

        var now = clock.GetUtcNow();

        foreach (var family in existing.GroupBy(row => (row.Key, row.Channel, row.Locale)))
        {
            var newest = family.Max(row => row.Version);

            foreach (var older in family.Where(row => row.Version < newest && row.IsActive))
            {
                older.Retire(now);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return added;
    }

    /// <summary>
    /// The version new mail renders from: the active one for the locale, else for the default locale.
    /// </summary>
    public static async Task<NotificationTemplate?> FindActiveAsync(
        AppDbContext dbContext,
        string key,
        NotificationChannel channel,
        string locale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var candidates = await dbContext.NotificationTemplates
            .AsNoTracking()
            .Where(t => t.Key == key
                        && t.Channel == channel
                        && t.RetiredAt == null
                        && (t.Locale == locale || t.Locale == NotificationTemplate.DefaultLocale))
            .ToListAsync(cancellationToken);

        return candidates
            .OrderByDescending(t => t.Locale == locale)
            .ThenByDescending(t => t.Version)
            .FirstOrDefault();
    }
}
