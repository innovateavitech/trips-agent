using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Catalog;

/// <summary>
/// Everything an agent decides about a departure, as one value: the date, the seats, the prices,
/// the deposit and the installment plan.
/// </summary>
/// <remarks>
/// The console edits a whole departure and sends the whole thing back, exactly as it does with a
/// product, so this is what both a create and a save carry. Nothing worked out from the seats —
/// the status, the counts, the cutoff instant — belongs here: those are the server's.
/// </remarks>
public sealed record DepartureTerms
{
    /// <summary>The day it leaves. A date, not an instant: a departure leaves on a day, not at a UTC moment.</summary>
    public DateOnly DepartureDate { get; init; }

    /// <summary>A group departure only runs once <see cref="MinPax"/> travellers have paid.</summary>
    public bool IsGroupDeparture { get; init; }

    /// <summary>The smallest party that makes it run. 1 on a departure that always runs.</summary>
    public int MinPax { get; init; }

    /// <summary>Every seat there is. <c>max_pax</c> tracks it until something needs them to differ.</summary>
    public int CapacityTotal { get; init; }

    /// <summary>Bookings close this many days before <see cref="DepartureDate"/>.</summary>
    public int CutoffDaysBefore { get; init; }

    public DepositType DepositType { get; init; }

    /// <summary>Set exactly when <see cref="DepositType"/> is <see cref="DepositType.Percent"/>.</summary>
    public int? DepositPercentBasisPoints { get; init; }

    /// <summary>Set exactly when <see cref="DepositType"/> is <see cref="DepositType.Fixed"/>.</summary>
    public Money? DepositAmountMinor { get; init; }

    /// <summary>Contiguous from a party of 1, the last one open-ended.</summary>
    public IReadOnlyList<PriceTierTerms> PriceTiers { get; init; } = [];

    /// <summary>The balance after the deposit, split into payments. Empty means it is due at the cutoff.</summary>
    public IReadOnlyList<InstallmentTerms> Installments { get; init; } = [];

    /// <summary>
    /// The same terms with the parts that do not apply cleared, and the sequences renumbered from 1
    /// in list order — so what is stored never contradicts itself.
    /// </summary>
    public DepartureTerms Normalised() => this with
    {
        MinPax = IsGroupDeparture ? MinPax : 1,
        DepositPercentBasisPoints = DepositType == DepositType.Percent ? DepositPercentBasisPoints : null,
        DepositAmountMinor = DepositType == DepositType.Fixed ? DepositAmountMinor : null,
        Installments = [.. Installments.Select((item, index) => item with { Sequence = index + 1 })],
    };
}

/// <summary>The price per traveller for a party of this size.</summary>
/// <param name="MinPax">The smallest party this price is for. 1 on the first tier.</param>
/// <param name="MaxPax">The largest. Null on the last tier: this size and up.</param>
public sealed record PriceTierTerms(int MinPax, int? MaxPax, Money PricePerPaxMinor);

/// <summary>One payment of the balance left after the deposit.</summary>
/// <param name="Sequence">1 for the first payment. Renumbered from list order on every save.</param>
/// <param name="PercentOfBalanceBasisPoints">A share of the balance: 5,000 is half. They add up to 10,000.</param>
public sealed record InstallmentTerms(
    int Sequence,
    InstallmentDueBasis DueBasis,
    int DueOffsetDays,
    int PercentOfBalanceBasisPoints);
