using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Catalog;

/// <summary>What one run of the reminder job did.</summary>
/// <param name="Reminded">Travellers reminded a payment is due or overdue.</param>
/// <param name="Flagged">Payments past their grace period that the agency was told about.</param>
public sealed record InstallmentReminderRun(int Reminded, int Flagged);

/// <summary>
/// Plan §3 job 11: reminds travellers about the payments they owe on a group departure, and tells
/// the agency about the ones nobody has paid.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is charged.</b> A reminder is an email and no more. Charging a saved card is job 12,
/// which the build plan leaves until after the MVP — so a traveller who ignores every reminder ends
/// up on an agency's list to call, not with a surprise debit.
/// </para>
/// <para>
/// <b>Nothing is cancelled either</b> (decision 13). Past the grace period the agency is told once,
/// with what it can do about it; the booking stands until a person decides otherwise. That is also
/// why an overdue installment does not go to the resolution queue: that queue means <i>refund this</i>,
/// and nobody has decided to refund anything.
/// </para>
/// <para>
/// <b>Sent at most once per stage.</b> Each installment records the closest reminder it has had, and
/// the stages count down, so a second run on the same day — or a second Worker — sends nothing.
/// </para>
/// </remarks>
public sealed partial class InstallmentReminders
{
    /// <summary>How long past its due date a payment goes before the agency is told.</summary>
    public const int GraceDays = 7;

    /// <summary>A ceiling on payments handled per run.</summary>
    public const int MaxPerRun = 500;

    /// <summary>The reminders sent before a payment falls due, furthest out first.</summary>
    private static readonly InstallmentReminderStage[] BeforeDue =
    [
        InstallmentReminderStage.SevenDays,
        InstallmentReminderStage.ThreeDays,
        InstallmentReminderStage.OneDay,
    ];

    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly INotifier _notifier;
    private readonly TimeProvider _clock;
    private readonly ILogger<InstallmentReminders> _logger;

    public InstallmentReminders(
        IAppDbContext db,
        IPlatformScope platformScope,
        INotifier notifier,
        TimeProvider clock,
        ILogger<InstallmentReminders> logger)
    {
        _db = db;
        _platformScope = platformScope;
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
    }

    public async Task<InstallmentReminderRun> RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "installment reminders — tells every agency's travellers what they owe on a group departure");

        var now = _clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var horizon = today.AddDays((int)InstallmentReminderStage.SevenDays);

        // Everything owed that is either due within the week or already past. Ordered by due date so
        // the most overdue is dealt with first when a run hits its ceiling.
        var due = await _db.BookingInstallments
            .Where(item => item.State == InstallmentState.Pending && item.DueDate <= horizon)
            .OrderBy(item => item.DueDate)
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return new InstallmentReminderRun(0, 0);
        }

        var scheduleIds = due.Select(item => item.ScheduleId).Distinct().ToList();

        var schedules = await _db.BookingPaymentSchedules.AsNoTracking()
            .Where(schedule => scheduleIds.Contains(schedule.Id))
            .ToDictionaryAsync(schedule => schedule.Id, cancellationToken);

        var titles = await DepartureTitlesAsync(schedules.Values, cancellationToken);
        var reminded = 0;
        var flagged = 0;

        foreach (var item in due)
        {
            if (!schedules.TryGetValue(item.ScheduleId, out var schedule))
            {
                continue;
            }

            var title = titles.GetValueOrDefault(schedule.DepartureId, "your departure");
            var daysLate = today.DayNumber - item.DueDate.DayNumber;
            var stage = StageFor(daysLate);

            if (stage is { } sending && item.RecordReminder(sending, now))
            {
                if (await RemindAsync(schedule, item, title, cancellationToken))
                {
                    reminded++;
                }
            }

            if (daysLate >= GraceDays && item.Flag(now))
            {
                await TellAgencyAsync(schedule, item, title, daysLate, cancellationToken);
                flagged++;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (reminded > 0 || flagged > 0)
        {
            LogRun(_logger, reminded, flagged);
        }

        return new InstallmentReminderRun(reminded, flagged);
    }

    /// <summary>
    /// The reminder a payment <paramref name="daysLate"/> days past its due date is owed. Negative
    /// days are days still to go. Null when it is further out than the first reminder.
    /// </summary>
    public static InstallmentReminderStage? StageFor(int daysLate)
    {
        if (daysLate > 0)
        {
            return InstallmentReminderStage.Overdue;
        }

        // -1 is "due tomorrow", so the stage that matches is the smallest one at least that far out.
        var daysToGo = -daysLate;

        foreach (var stage in BeforeDue.Reverse())
        {
            if (daysToGo <= (int)stage)
            {
                return stage;
            }
        }

        return null;
    }

    private async Task<bool> RemindAsync(
        BookingPaymentSchedule schedule,
        BookingInstallment item,
        string title,
        CancellationToken cancellationToken)
    {
        // No address, nothing to send. The payment still moves through its stages, so the agency's
        // overdue flag arrives on time even for a booking taken over the counter.
        if (schedule.ContactEmail is null)
        {
            return false;
        }

        return await _notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                schedule.AgencyId,
                NotificationTemplateCatalog.DepartureInstallmentReminder,
                schedule.ContactEmail,
                schedule.ContactName,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bookingReference"] = await OrderNumberAsync(schedule.OrderLineId, cancellationToken),
                    ["departureTitle"] = title,
                    ["paymentLabel"] = item.Label,
                    ["amountDue"] = Amount(schedule.Currency, item.AmountMinor),
                    ["dueDate"] = Day(item.DueDate),
                },
                // The stage is in the key, so each of T-7, T-3, T-1 and overdue is its own email.
                $"{NotificationTemplateCatalog.DepartureInstallmentReminder}:{item.Id}:{item.LastReminderStage}"),
            cancellationToken);
    }

    private async Task TellAgencyAsync(
        BookingPaymentSchedule schedule,
        BookingInstallment item,
        string title,
        int daysLate,
        CancellationToken cancellationToken)
    {
        var recipient = await _db.Users.AsNoTracking()
            .Where(user => user.AgencyId == schedule.AgencyId)
            .OrderBy(user => user.CreatedAt)
            .Select(user => new { user.Id, user.Email, user.FirstName })
            .FirstOrDefaultAsync(cancellationToken);

        if (recipient is null)
        {
            LogNoRecipient(_logger, schedule.AgencyId, item.Id);
            return;
        }

        await _notifier.QueueEmailAsync(
            new EmailNotificationRequest(
                schedule.AgencyId,
                NotificationTemplateCatalog.DepartureInstallmentOverdue,
                recipient.Email,
                recipient.FirstName,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["bookingReference"] = await OrderNumberAsync(schedule.OrderLineId, cancellationToken),
                    ["departureTitle"] = title,
                    ["travellerName"] = schedule.ContactName,
                    ["paymentLabel"] = item.Label,
                    ["amountDue"] = Amount(schedule.Currency, item.AmountMinor),
                    ["dueDate"] = Day(item.DueDate),
                    ["daysOverdue"] = daysLate.ToString(CultureInfo.InvariantCulture),
                },
                $"{NotificationTemplateCatalog.DepartureInstallmentOverdue}:{item.Id}",
                recipient.Id),
            cancellationToken);
    }

    private async Task<Dictionary<Guid, string>> DepartureTitlesAsync(
        IEnumerable<BookingPaymentSchedule> schedules,
        CancellationToken cancellationToken)
    {
        var departureIds = schedules.Select(schedule => schedule.DepartureId).Distinct().ToList();

        return await (
            from departure in _db.Departures.AsNoTracking()
            join product in _db.Products.AsNoTracking() on departure.ProductId equals product.Id
            where departureIds.Contains(departure.Id)
            select new { departure.Id, product.Title })
            .ToDictionaryAsync(row => row.Id, row => row.Title, cancellationToken);
    }

    private async Task<string> OrderNumberAsync(Guid orderLineId, CancellationToken cancellationToken) =>
        await (
            from line in _db.OrderLines.AsNoTracking()
            join order in _db.Orders.AsNoTracking() on line.OrderId equals order.Id
            where line.Id == orderLineId
            select order.OrderNumber)
            .FirstOrDefaultAsync(cancellationToken)
        ?? string.Empty;

    /// <summary>
    /// "NGN 1,500.00". Minor units are divided out only here, at the edge, where the number stops
    /// being arithmetic and becomes something a person reads.
    /// </summary>
    private static string Amount(string currency, Money money) =>
        string.Create(CultureInfo.InvariantCulture, $"{currency} {money.AmountMinor / 100m:N2}");

    private static string Day(DateOnly date) => date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    [LoggerMessage(EventId = 6131, Level = LogLevel.Information, Message = "Sent {Reminded} installment reminders and flagged {Flagged} overdue payments.")]
    private static partial void LogRun(ILogger logger, int reminded, int flagged);

    [LoggerMessage(EventId = 6132, Level = LogLevel.Warning, Message = "Agency {AgencyId} has no user to tell about overdue installment {InstallmentId}.")]
    private static partial void LogNoRecipient(ILogger logger, Guid agencyId, Guid installmentId);
}
