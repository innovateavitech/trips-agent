using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Catalog;

/// <summary>
/// What a departure has to look like before it can be stored (build plan F6).
/// </summary>
/// <remarks>
/// <para>
/// Every problem at once, keyed by the field the console names, so the editor can put each message
/// next to its input in one pass. The field keys are the console's own —
/// <c>priceTiers.1.maxPax</c>, <c>installments.0.share</c> — and are part of the contract.
/// </para>
/// <para>
/// These are the rules the stand-in in <c>features/departures/departure-rules.ts</c> applies in the
/// browser. The server applies them again, because a browser is not a place to enforce anything.
/// </para>
/// </remarks>
public static class DepartureRules
{
    /// <summary>100%, in basis points. Shares of the balance add up to exactly this.</summary>
    public const int FullBasisPoints = 10_000;

    /// <summary>More seats than any coach, boat or charter an agent sells by the seat.</summary>
    public const int MaxCapacity = 5_000;

    /// <summary>Enough tiers for any party-size ladder, and a bound on what a request can send.</summary>
    public const int MaxPriceTiers = 20;

    /// <summary>Two years of monthly payments, and a bound on what a request can send.</summary>
    public const int MaxInstallments = 24;

    /// <summary>Far enough ahead for any departure, and a bound on a cutoff that would underflow.</summary>
    public const int MaxCutoffDaysBefore = 3_650;

    /// <summary>Every reason <paramref name="terms"/> cannot be stored. Empty when there is none.</summary>
    /// <param name="today">Today where the agency is, so a departure tomorrow is not refused at 23:30 UTC.</param>
    public static IReadOnlyList<ProductProblem> Validate(DepartureTerms terms, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var problems = new List<ProductProblem>();

        if (terms.DepartureDate <= today)
        {
            problems.Add(new ProductProblem("departureDate", "A departure has to be in the future."));
        }

        if (terms.CapacityTotal < 1)
        {
            problems.Add(new ProductProblem("capacityTotal", "At least one seat."));
        }
        else if (terms.CapacityTotal > MaxCapacity)
        {
            problems.Add(new ProductProblem("capacityTotal", $"At most {MaxCapacity} seats."));
        }

        if (terms.IsGroupDeparture)
        {
            if (terms.MinPax < 1)
            {
                problems.Add(new ProductProblem("minPax", "At least one traveller."));
            }
            else if (terms.MinPax > terms.CapacityTotal)
            {
                problems.Add(new ProductProblem("minPax", "More than the seats there are."));
            }
        }

        if (terms.CutoffDaysBefore < 0)
        {
            problems.Add(new ProductProblem("cutoffDaysBefore", "Zero or more days."));
        }
        else if (terms.CutoffDaysBefore > MaxCutoffDaysBefore)
        {
            problems.Add(new ProductProblem(
                "cutoffDaysBefore", $"At most {MaxCutoffDaysBefore} days before it leaves."));
        }

        ValidatePriceTiers(terms, problems);
        ValidateDeposit(terms, problems);
        ValidateInstallments(terms, problems);

        return problems;
    }

    /// <summary>
    /// The cheapest seat on offer, whatever the party size — what a fixed deposit is measured
    /// against. Null when no tier has a price yet.
    /// </summary>
    public static Money? LowestPrice(IReadOnlyList<PriceTierTerms> tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        return tiers.Count == 0 ? null : tiers.Min(tier => tier.PricePerPaxMinor);
    }

    /// <summary>The price per traveller for a party of this size, or null when no tier covers it.</summary>
    public static Money? PriceForParty(IReadOnlyList<PriceTierTerms> tiers, int pax)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        return tiers
            .FirstOrDefault(tier => pax >= tier.MinPax && (tier.MaxPax is null || pax <= tier.MaxPax))
            ?.PricePerPaxMinor;
    }

    private static void ValidatePriceTiers(DepartureTerms terms, List<ProductProblem> problems)
    {
        if (terms.PriceTiers.Count == 0)
        {
            problems.Add(new ProductProblem("priceTiers", "Set a price per traveller."));
            return;
        }

        if (terms.PriceTiers.Count > MaxPriceTiers)
        {
            problems.Add(new ProductProblem("priceTiers", $"At most {MaxPriceTiers} party sizes."));
            return;
        }

        // "Contiguous from 1, the last one open-ended": every tier starts one above the last one's
        // upper bound, so no party size can fall in a gap or match two prices at once.
        var expectedMin = 1;
        var contiguous = true;

        for (var index = 0; index < terms.PriceTiers.Count; index++)
        {
            var tier = terms.PriceTiers[index];
            var last = index == terms.PriceTiers.Count - 1;

            if (contiguous && tier.MinPax != expectedMin)
            {
                problems.Add(new ProductProblem(
                    "priceTiers", "Party sizes have to follow on from 1, with no gaps or overlaps."));
                contiguous = false;
            }

            if (tier.MaxPax is null)
            {
                if (!last)
                {
                    problems.Add(new ProductProblem(
                        $"priceTiers.{index}.maxPax", "Only the last party size can be open-ended."));
                    contiguous = false;
                }
            }
            else if (tier.MaxPax < tier.MinPax)
            {
                problems.Add(new ProductProblem($"priceTiers.{index}.maxPax", $"At least {tier.MinPax}."));
                contiguous = false;
            }
            else
            {
                expectedMin = tier.MaxPax.Value + 1;
            }

            if (tier.PricePerPaxMinor.AmountMinor <= 0)
            {
                problems.Add(new ProductProblem($"priceTiers.{index}.price", "Set a price."));
            }
        }
    }

    private static void ValidateDeposit(DepartureTerms terms, List<ProductProblem> problems)
    {
        switch (terms.DepositType)
        {
            case DepositType.Percent:
                {
                    var share = terms.DepositPercentBasisPoints ?? 0;

                    if (share <= 0 || share > FullBasisPoints)
                    {
                        problems.Add(new ProductProblem("deposit", "More than 0%, and at most 100%."));
                    }

                    break;
                }

            case DepositType.Fixed:
                {
                    var amount = terms.DepositAmountMinor ?? Money.Zero;
                    var lowest = LowestPrice(terms.PriceTiers);

                    if (amount.AmountMinor <= 0)
                    {
                        problems.Add(new ProductProblem("deposit", "Set the deposit amount."));
                    }
                    else if (lowest is { } cheapest && amount > cheapest)
                    {
                        problems.Add(new ProductProblem("deposit", "More than the price of a seat."));
                    }

                    break;
                }

            case DepositType.None:
                break;

            default:
                problems.Add(new ProductProblem("depositType", "Choose None, Percent or Fixed."));
                break;
        }
    }

    private static void ValidateInstallments(DepartureTerms terms, List<ProductProblem> problems)
    {
        if (terms.Installments.Count == 0)
        {
            return;
        }

        if (terms.Installments.Count > MaxInstallments)
        {
            problems.Add(new ProductProblem("installments", $"At most {MaxInstallments} payments."));
            return;
        }

        var total = terms.Installments.Sum(item => (long)item.PercentOfBalanceBasisPoints);

        if (total != FullBasisPoints)
        {
            problems.Add(new ProductProblem(
                "installments",
                $"The payments add up to {Percent(total)}% of the balance, not 100%."));
        }

        for (var index = 0; index < terms.Installments.Count; index++)
        {
            var item = terms.Installments[index];

            if (item.DueOffsetDays < 0 || item.DueOffsetDays > MaxCutoffDaysBefore)
            {
                problems.Add(new ProductProblem($"installments.{index}.offset", "Zero or more days."));
            }

            if (item.PercentOfBalanceBasisPoints <= 0)
            {
                problems.Add(new ProductProblem($"installments.{index}.share", "More than 0%."));
            }

            if (!Enum.IsDefined(item.DueBasis))
            {
                problems.Add(new ProductProblem(
                    $"installments.{index}.dueBasis", "Choose FromBooking or BeforeDeparture."));
            }
        }
    }

    /// <summary>Basis points as a percentage for a message: 3,050 reads "30.5".</summary>
    private static string Percent(long basisPoints) =>
        (basisPoints / 100m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
