using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Api.Authorization;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Pricing;
using TripsAgent.Contracts.Pricing;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Pricing;

namespace TripsAgent.Api.Pricing;

/// <summary>Markup rules, and reading back a quote with or without its margin.</summary>
public static class PricingEndpoints
{
    public static IEndpointRouteBuilder MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/pricing")
            .WithTags("Pricing")
            .RequireAuthorization();

        // A markup rule is margin: knowing the rule and the price tells you the net rate. So even
        // listing them needs margin.view, and changing them needs margin.edit.
        group.MapGet("/markup-rules", async (MarkupRuleService rules, CancellationToken cancellationToken) =>
                Results.Ok((await rules.ListAsync(cancellationToken)).Select(ToResponse).ToList()))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.MarginView))
            .WithName("ListMarkupRules")
            .Produces<List<MarkupRuleResponse>>();

        group.MapPost("/markup-rules", async (
                MarkupRuleRequest request,
                MarkupRuleService rules,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                if (!TryReadTerms(request, clock.GetUtcNow(), out var terms, out var problem))
                {
                    return problem;
                }

                return ToResult(await rules.CreateAsync(terms, cancellationToken), created: true);
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.MarginEdit))
            .WithName("CreateMarkupRule")
            .Produces<MarkupRuleResponse>(StatusCodes.Status201Created);

        // PUT replaces rather than updates: the old rule is retired and the response is the new
        // one, with a new id. Quotes priced by the old rule keep naming the terms that priced them.
        group.MapPut("/markup-rules/{ruleId:guid}", async (
                Guid ruleId,
                MarkupRuleRequest request,
                MarkupRuleService rules,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                ArgumentNullException.ThrowIfNull(request);

                if (!TryReadTerms(request, clock.GetUtcNow(), out var terms, out var problem))
                {
                    return problem;
                }

                return ToResult(await rules.ReplaceAsync(ruleId, terms, cancellationToken), created: false);
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.MarginEdit))
            .WithName("ReplaceMarkupRule")
            .Produces<MarkupRuleResponse>();

        group.MapPost("/markup-rules/{ruleId:guid}/retire", async (
                Guid ruleId,
                MarkupRuleService rules,
                CancellationToken cancellationToken) =>
                ToResult(await rules.RetireAsync(ruleId, cancellationToken), created: false))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.MarginEdit))
            .WithName("RetireMarkupRule")
            .Produces<MarkupRuleResponse>();

        // Anyone who sells can read a quote; only margin.view sees what it is made of.
        group.MapGet("/quotes/{quoteId:guid}", async (
                Guid quoteId,
                ClaimsPrincipal user,
                IAppDbContext db,
                CancellationToken cancellationToken) =>
            {
                var quote = await db.PriceQuotes
                    .AsNoTracking()
                    .FirstOrDefaultAsync(candidate => candidate.Id == quoteId, cancellationToken);

                if (quote is null)
                {
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No quote with that id.");
                }

                return CanViewMargin(user)
                    ? Results.Ok(WithMargin(quote))
                    : Results.Ok(WithoutMargin(quote));
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.BookingSearch))
            .WithName("GetPriceQuote")
            .Produces<PriceQuoteResponse>()
            .Produces<PriceQuoteWithMarginResponse>();

        return app;
    }

    /// <summary>True when the caller's token carries <c>margin.view</c>.</summary>
    public static bool CanViewMargin(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.HasClaim(TripsClaimTypes.Permission, PermissionCodes.MarginView);
    }

    private static PriceQuoteResponse WithoutMargin(PriceQuote quote) =>
        new(quote.Id, quote.ProductType.ToString(), quote.Currency, quote.GrossAmountMinor.AmountMinor, quote.CreatedAt);

    private static PriceQuoteWithMarginResponse WithMargin(PriceQuote quote) =>
        new(
            quote.Id,
            quote.ProductType.ToString(),
            quote.Currency,
            quote.GrossAmountMinor.AmountMinor,
            quote.NetAmountMinor.AmountMinor,
            quote.MarkupAmountMinor.AmountMinor,
            quote.MarkupRuleId,
            quote.CreatedAt);

    private static MarkupRuleResponse ToResponse(MarkupRule rule) =>
        new(
            rule.Id,
            rule.Scope.ToString(),
            rule.ProductType?.ToString(),
            rule.ProductId,
            rule.SupplierCode,
            rule.Currency,
            rule.CalculationType.ToString(),
            rule.PercentBasisPoints,
            rule.ValueMinor?.AmountMinor,
            rule.MinMarkupMinor?.AmountMinor,
            rule.MaxMarkupMinor?.AmountMinor,
            rule.Priority,
            rule.AppliesToSubAgents,
            rule.EffectiveFrom,
            rule.EffectiveTo,
            rule.SupersededById);

    private static IResult ToResult(MarkupRuleChangeOutcome outcome, bool created) => outcome switch
    {
        MarkupRuleChangeOutcome.Saved saved when created =>
            Results.Created($"/api/v1/pricing/markup-rules/{saved.Rule.Id}", ToResponse(saved.Rule)),

        MarkupRuleChangeOutcome.Saved saved => Results.Ok(ToResponse(saved.Rule)),

        MarkupRuleChangeOutcome.Invalid invalid => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "That rule does not add up.",
            detail: invalid.Reason),

        MarkupRuleChangeOutcome.NotFound => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "No markup rule with that id."),

        // 409: the request was well-formed, but the rule is no longer in a state to change.
        MarkupRuleChangeOutcome.NoLongerEditable stale => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "That rule can no longer be changed.",
            detail: stale.Reason),

        _ => throw new InvalidOperationException($"Unhandled outcome {outcome.GetType().Name}."),
    };

    private static bool TryReadTerms(
        MarkupRuleRequest request,
        DateTimeOffset now,
        out MarkupRuleTerms terms,
        out IResult problem)
    {
        terms = null!;
        problem = Results.Empty;

        if (!TryParseEnum<MarkupScope>(request.Scope, out var scope))
        {
            problem = BadRequest("scope must be one of Global, Supplier, ProductType or Product.");
            return false;
        }

        if (!TryParseEnum<MarkupCalculationType>(request.CalculationType, out var calculation))
        {
            problem = BadRequest("calculationType must be Percentage or Fixed.");
            return false;
        }

        PricedProductType? productType = null;

        if (request.ProductType is not null)
        {
            if (!TryParseEnum<PricedProductType>(request.ProductType, out var parsed))
            {
                problem = BadRequest("productType must be one of Flight, Bus, Tour, Visa or GroupDeparture.");
                return false;
            }

            productType = parsed;
        }

        terms = new MarkupRuleTerms
        {
            Scope = scope,
            ProductType = productType,
            ProductId = request.ProductId,
            SupplierCode = request.SupplierCode,
            Currency = request.Currency ?? string.Empty,
            CalculationType = calculation,
            PercentBasisPoints = request.PercentBasisPoints,
            ValueMinor = request.ValueMinor is { } value ? new Money(value) : null,
            MinMarkupMinor = request.MinMarkupMinor is { } min ? new Money(min) : null,
            MaxMarkupMinor = request.MaxMarkupMinor is { } max ? new Money(max) : null,
            Priority = request.Priority,
            AppliesToSubAgents = request.AppliesToSubAgents,
            EffectiveFrom = request.EffectiveFrom ?? now,
            EffectiveTo = request.EffectiveTo,
        };

        return true;
    }

    /// <summary>
    /// Parses an enum by name only. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> also
    /// accepts "4" and even "99", which would let a typo through as a number nobody meant.
    /// </summary>
    private static bool TryParseEnum<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;

        return !string.IsNullOrWhiteSpace(value)
               && !value.Trim().All(character => char.IsDigit(character) || character == '-')
               && Enum.TryParse(value.Trim(), ignoreCase: true, out result)
               && Enum.IsDefined(result);
    }

    private static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "That rule does not add up.", detail: detail);
}
