using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Catalog;

/// <summary>Where one payment on a booking's schedule stands.</summary>
public enum InstallmentState
{
    /// <summary>Owed. The only state a reminder is ever sent for.</summary>
    Pending = 1,

    /// <summary>Settled.</summary>
    Paid = 2,

    /// <summary>The booking went away — refunded, or the departure was called off.</summary>
    Cancelled = 3,
}

/// <summary>
/// Which reminder was last sent for a payment (plan §3 job 11).
/// </summary>
/// <remarks>
/// The values count down to the due date on purpose: "send stage <i>s</i> only if the last one sent
/// was further out than <i>s</i>" is then a single comparison, and a job that runs twice in a day —
/// or two Workers running it at once — cannot send the same reminder twice.
/// </remarks>
public enum InstallmentReminderStage
{
    /// <summary>Already past its due date.</summary>
    Overdue = 0,

    /// <summary>Due tomorrow.</summary>
    OneDay = 1,

    /// <summary>Due in three days.</summary>
    ThreeDays = 3,

    /// <summary>Due in a week.</summary>
    SevenDays = 7,
}

/// <summary>
/// What one booking on a departure pays, and when: a snapshot of the departure's terms taken on the
/// day it was booked (build plan F6, plan §3 job 11).
/// </summary>
/// <remarks>
/// <para>
/// <b>A snapshot, not a view.</b> The departure's installment plan is a template; this is the bill.
/// An agent who changes the deposit or the payment dates next month must not move a payment a
/// traveller has already been told about — CLAUDE.md rule 5, the same reason an order line freezes
/// its price.
/// </para>
/// <para>
/// <b>Nothing is ever charged from here.</b> The MVP sends reminders and no more; automatic charging
/// of a saved card is plan §3 job 12 and waits until after the MVP. So this carries who to remind
/// and when, and not a payment instrument.
/// </para>
/// <para>
/// The contact is carried here rather than looked up because a group departure has no supplier
/// booking to read passengers from, and a guest checkout has no customer record either.
/// </para>
/// </remarks>
public sealed class BookingPaymentSchedule : Entity, IAuditableEntity, ITenantScoped, IAuditLogged
{
    private readonly List<BookingInstallment> _items = [];

    private BookingPaymentSchedule()
    {
        Currency = string.Empty;
        ContactName = string.Empty;
    }

    /// <summary>The bill for one booking, from the payments <c>DeparturePaymentPlan</c> worked out.</summary>
    public static BookingPaymentSchedule Create(
        Guid agencyId,
        Guid departureId,
        Guid orderLineId,
        int paxCount,
        string currency,
        Money pricePerPaxMinor,
        string contactName,
        string? contactEmail,
        DateOnly bookedOn,
        IReadOnlyList<ScheduledPayment> payments,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(agencyId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(departureId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(orderLineId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(paxCount, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(contactName);
        ArgumentNullException.ThrowIfNull(payments);

        var schedule = new BookingPaymentSchedule
        {
            AgencyId = agencyId,
            DepartureId = departureId,
            OrderLineId = orderLineId,
            PaxCount = paxCount,
            Currency = currency.Trim().ToUpperInvariant(),
            PricePerPaxMinor = pricePerPaxMinor,
            ContactName = contactName.Trim(),
            ContactEmail = string.IsNullOrWhiteSpace(contactEmail) ? null : contactEmail.Trim(),
            BookedOn = bookedOn,
            CreatedAt = now.ToUniversalTime(),
            UpdatedAt = now.ToUniversalTime(),
        };

        foreach (var payment in payments)
        {
            schedule._items.Add(BookingInstallment.Create(agencyId, schedule.Id, payment, now));
        }

        return schedule;
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid DepartureId { get; private set; }

    /// <summary>The order line that bought the seats. One schedule per line.</summary>
    public Guid OrderLineId { get; private set; }

    public int PaxCount { get; private set; }

    public string Currency { get; private set; }

    /// <summary>The tier price the party was sold at, frozen on the day.</summary>
    public Money PricePerPaxMinor { get; private set; }

    /// <summary>Who the reminders are addressed to.</summary>
    public string ContactName { get; private set; }

    /// <summary>Where they go. Null when the booking carries no email, and then nothing is sent.</summary>
    public string? ContactEmail { get; private set; }

    /// <summary>The day it was booked: what <c>FromBooking</c> offsets are counted from.</summary>
    public DateOnly BookedOn { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The payments, in sequence.</summary>
    public IReadOnlyList<BookingInstallment> Items => _items;

    /// <summary>What the whole booking costs: every payment added up.</summary>
    public Money TotalMinor => new(_items.Sum(item => item.AmountMinor.AmountMinor));

    /// <summary>What is still owed.</summary>
    public Money OutstandingMinor =>
        new(_items.Where(item => item.State == InstallmentState.Pending).Sum(item => item.AmountMinor.AmountMinor));

    /// <summary>Closes every payment still owed — the booking was refunded, or the departure called off.</summary>
    public void Cancel(DateTimeOffset now)
    {
        foreach (var item in _items)
        {
            item.Cancel(now);
        }

        UpdatedAt = now.ToUniversalTime();
    }
}

/// <summary>One payment on a booking's schedule: what is owed, when, and what has been sent about it.</summary>
public sealed class BookingInstallment : Entity, IAuditableEntity, ITenantScoped
{
    private BookingInstallment()
    {
        Label = string.Empty;
    }

    internal static BookingInstallment Create(
        Guid agencyId,
        Guid scheduleId,
        ScheduledPayment payment,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return new BookingInstallment
        {
            AgencyId = agencyId,
            ScheduleId = scheduleId,
            Sequence = payment.Sequence,
            Label = payment.Label,
            DueDate = payment.DueDate,
            AmountMinor = payment.AmountMinor,
            State = InstallmentState.Pending,
            CreatedAt = now.ToUniversalTime(),
            UpdatedAt = now.ToUniversalTime(),
        };
    }

    /// <inheritdoc />
    public Guid AgencyId { get; private set; }

    public Guid ScheduleId { get; private set; }

    /// <summary>1 for the deposit, or for the first payment when there is no deposit.</summary>
    public int Sequence { get; private set; }

    /// <summary>What the traveller sees: "Deposit", "Balance", "Payment 2".</summary>
    public string Label { get; private set; }

    public DateOnly DueDate { get; private set; }

    public Money AmountMinor { get; private set; }

    public InstallmentState State { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>The reminder last sent, or null when none has been.</summary>
    public InstallmentReminderStage? LastReminderStage { get; private set; }

    public DateTimeOffset? LastReminderAt { get; private set; }

    /// <summary>
    /// When the agency was told this payment was past its grace period. Set once: the agency is told
    /// what to do about it, and decision 13 forbids cancelling it for them.
    /// </summary>
    public DateTimeOffset? FlaggedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Whether <paramref name="stage"/> is still to be sent. False once one this close has been.</summary>
    public bool NeedsReminder(InstallmentReminderStage stage) =>
        State == InstallmentState.Pending && (LastReminderStage is null || LastReminderStage > stage);

    /// <summary>Records that the reminder for <paramref name="stage"/> went out. Idempotent.</summary>
    /// <returns>True when this call is the one that recorded it.</returns>
    public bool RecordReminder(InstallmentReminderStage stage, DateTimeOffset now)
    {
        if (!NeedsReminder(stage))
        {
            return false;
        }

        LastReminderStage = stage;
        LastReminderAt = now.ToUniversalTime();
        UpdatedAt = now.ToUniversalTime();

        return true;
    }

    /// <summary>Records that the agency has been told this is past its grace period. Idempotent.</summary>
    /// <returns>True when this call is the one that flagged it.</returns>
    public bool Flag(DateTimeOffset now)
    {
        if (State != InstallmentState.Pending || FlaggedAt is not null)
        {
            return false;
        }

        FlaggedAt = now.ToUniversalTime();
        UpdatedAt = now.ToUniversalTime();

        return true;
    }

    /// <summary>Settles it. Idempotent.</summary>
    /// <returns>True when this call is the one that settled it.</returns>
    public bool MarkPaid(DateTimeOffset now)
    {
        if (State != InstallmentState.Pending)
        {
            return false;
        }

        State = InstallmentState.Paid;
        PaidAt = now.ToUniversalTime();
        UpdatedAt = now.ToUniversalTime();

        return true;
    }

    /// <summary>Closes it unpaid. Idempotent, and never touches one already paid.</summary>
    internal bool Cancel(DateTimeOffset now)
    {
        if (State != InstallmentState.Pending)
        {
            return false;
        }

        State = InstallmentState.Cancelled;
        UpdatedAt = now.ToUniversalTime();

        return true;
    }
}
