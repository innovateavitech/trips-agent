using System.Security.Claims;
using TripsAgent.Api.Authorization;
using TripsAgent.Api.RateLimiting;
using TripsAgent.Application.Identity;
using TripsAgent.Application.RateLimiting;
using TripsAgent.Application.Tenancy.SubAgents;
using TripsAgent.Contracts.Tenancy;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Tenancy.SubAgents;

namespace TripsAgent.Api.Tenancy;

/// <summary>
/// A principal's sub-agent network (feature F10, issue 63): inviting agents beneath it, saying what
/// each may sell and see, capping what each may spend, and reading the network's figures.
/// </summary>
/// <remarks>
/// <para>
/// Everything under <c>/sub-agents</c> needs <c>subagent.manage</c> and is refused for a sub-agent
/// — the service checks that the caller is a principal, so the depth cap holds at the API as well
/// as in the domain and in the database.
/// </para>
/// <para>
/// <b>Margin visibility is a choice of response type, not a null.</b> The network performance
/// endpoint serialises <see cref="NetworkPerformanceResponse"/> or
/// <see cref="NetworkPerformanceWithMarginResponse"/> depending on the caller's
/// <c>margin.view</c> claim — and a sub-agent whose principal has denied that claim never holds
/// it, because <see cref="SubAgentPermissionMiddleware"/> strips it off the token on every
/// request. The margin keys are then absent from the JSON, not null in it.
/// </para>
/// </remarks>
public static class SubAgentEndpoints
{
    public static IEndpointRouteBuilder MapSubAgentEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapNetwork(app);
        MapScopes(app);
        MapPermissions(app);
        MapAllowances(app);
        MapReporting(app);
        MapInvitations(app);

        return app;
    }

    // ------------------------------------------------------------------------ the network

    private static void MapNetwork(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sub-agents")
            .WithTags("Sub-agent network")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.SubAgentManage))
            .AddEndpointFilter<SubAgentRefusalFilter>();

        group.MapGet("/", async (SubAgentNetworkService network, CancellationToken cancellationToken) =>
            {
                var result = await network.ListAsync(cancellationToken);

                return Results.Ok(new SubAgentListResponse(
                    [.. result.SubAgents.Select(ToResponse)],
                    result.MaxSubAgents,
                    result.CanAddAnother,
                    result.CannotAddReason));
            })
            .WithName("ListSubAgents")
            .Produces<SubAgentListResponse>();

        group.MapPost("/", async (
                InviteSubAgentRequest request,
                SubAgentNetworkService network,
                CancellationToken cancellationToken) =>
            {
                if (request is null)
                {
                    return Results.BadRequest();
                }

                var invited = await network.InviteAsync(
                    request.LegalName, request.TradingName, request.Email, cancellationToken);

                return Results.Created(
                    $"/api/v1/sub-agents/{invited.AgencyId}",
                    new InviteSubAgentResponse(
                        invited.AgencyId, invited.Email, invited.InvitationToken, invited.ExpiresAt));
            })
            .WithName("InviteSubAgent")
            .Produces<InviteSubAgentResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{subAgencyId:guid}/freeze", (
                Guid subAgencyId,
                SubAgentStandingRequest request,
                SubAgentNetworkService network,
                CancellationToken cancellationToken) =>
                NoContentAfter(network.FreezeAsync(subAgencyId, request?.Reason ?? string.Empty, cancellationToken)))
            .WithName("FreezeSubAgent")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{subAgencyId:guid}/unfreeze", (
                Guid subAgencyId,
                SubAgentStandingRequest request,
                SubAgentNetworkService network,
                CancellationToken cancellationToken) =>
                NoContentAfter(network.UnfreezeAsync(subAgencyId, request?.Reason ?? string.Empty, cancellationToken)))
            .WithName("UnfreezeSubAgent")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{subAgencyId:guid}/revoke", (
                Guid subAgencyId,
                SubAgentStandingRequest request,
                SubAgentNetworkService network,
                CancellationToken cancellationToken) =>
                NoContentAfter(network.RevokeAsync(subAgencyId, request?.Reason ?? string.Empty, cancellationToken)))
            .WithName("RevokeSubAgent")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // ------------------------------------------------------------------------ scopes

    private static void MapScopes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sub-agents/{subAgencyId:guid}/scopes")
            .WithTags("Sub-agent network")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.SubAgentManage))
            .AddEndpointFilter<SubAgentRefusalFilter>();

        group.MapGet("/", async (
                Guid subAgencyId,
                SubAgentScopeService scopes,
                CancellationToken cancellationToken) =>
                Results.Ok((await scopes.ListAsync(subAgencyId, cancellationToken))
                    .Select(scope => new SubAgentScopeResponse(
                        scope.Id, scope.ProductType.ToString(), scope.SupplierId, scope.SupplierName))
                    .ToList()))
            .WithName("ListSubAgentScopes")
            .Produces<IReadOnlyList<SubAgentScopeResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (
                Guid subAgencyId,
                GrantSubAgentScopeRequest request,
                SubAgentScopeService scopes,
                CancellationToken cancellationToken) =>
            {
                if (request is null || !Enum.TryParse<SellableProductType>(request.ProductType, true, out var productType))
                {
                    return Invalid(
                        "productType",
                        request?.ProductType,
                        Enum.GetNames<SellableProductType>());
                }

                await scopes.GrantAsync(subAgencyId, productType, request.SupplierId, cancellationToken);

                return Results.NoContent();
            })
            .WithName("GrantSubAgentScope")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{scopeId:guid}", (
                Guid subAgencyId,
                Guid scopeId,
                SubAgentScopeService scopes,
                CancellationToken cancellationToken) =>
                NoContentAfter(scopes.RevokeAsync(subAgencyId, scopeId, cancellationToken)))
            .WithName("RevokeSubAgentScope")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // ------------------------------------------------------------------------ permissions

    private static void MapPermissions(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sub-agents/{subAgencyId:guid}/permissions")
            .WithTags("Sub-agent network")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.SubAgentManage))
            .AddEndpointFilter<SubAgentRefusalFilter>();

        group.MapGet("/", async (
                Guid subAgencyId,
                SubAgentPermissionService permissions,
                CancellationToken cancellationToken) =>
                Results.Ok((await permissions.MatrixAsync(subAgencyId, cancellationToken))
                    .Select(row => new SubAgentPermissionResponse(
                        row.Code, row.Category, row.Description, row.IsDenied, row.Reason))
                    .ToList()))
            .WithName("ListSubAgentPermissions")
            .Produces<IReadOnlyList<SubAgentPermissionResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/deny", (
                Guid subAgencyId,
                DenyPermissionRequest request,
                SubAgentPermissionService permissions,
                CancellationToken cancellationToken) =>
                NoContentAfter(permissions.DenyAsync(
                    subAgencyId,
                    request?.PermissionCode ?? string.Empty,
                    request?.Reason ?? string.Empty,
                    cancellationToken)))
            .WithName("DenySubAgentPermission")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{permissionCode}", (
                Guid subAgencyId,
                string permissionCode,
                SubAgentPermissionService permissions,
                CancellationToken cancellationToken) =>
                NoContentAfter(permissions.AllowAsync(subAgencyId, permissionCode, cancellationToken)))
            .WithName("AllowSubAgentPermission")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // ------------------------------------------------------------------------ allowances

    private static void MapAllowances(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sub-agents/{subAgencyId:guid}/allowance")
            .WithTags("Sub-agent network")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.SubAgentManage))
            .AddEndpointFilter<SubAgentRefusalFilter>();

        group.MapGet("/", async (
                Guid subAgencyId,
                SubAgentAllowanceService allowances,
                CancellationToken cancellationToken) =>
            {
                var allowance = await allowances.GetAsync(subAgencyId, cancellationToken);

                return allowance is null ? Results.NoContent() : Results.Ok(ToResponse(allowance));
            })
            .WithName("GetSubAgentAllowance")
            .Produces<AllowanceResponse>()
            .Produces(StatusCodes.Status204NoContent);

        group.MapPut("/", async (
                Guid subAgencyId,
                SetAllowanceRequest request,
                SubAgentAllowanceService allowances,
                CancellationToken cancellationToken) =>
            {
                if (request is null || !Enum.TryParse<AllowancePeriod>(request.Period, true, out var period))
                {
                    return Invalid("period", request?.Period, Enum.GetNames<AllowancePeriod>());
                }

                return Results.Ok(ToResponse(
                    await allowances.SetAsync(subAgencyId, request.LimitMinor, period, cancellationToken)));
            })
            .WithName("SetSubAgentAllowance")
            .Produces<AllowanceResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/freeze", async (
                Guid subAgencyId,
                SubAgentAllowanceService allowances,
                CancellationToken cancellationToken) =>
                Results.Ok(ToResponse(await allowances.FreezeAsync(subAgencyId, cancellationToken))))
            .WithName("FreezeSubAgentAllowance")
            .Produces<AllowanceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/unfreeze", async (
                Guid subAgencyId,
                SubAgentAllowanceService allowances,
                CancellationToken cancellationToken) =>
                Results.Ok(ToResponse(await allowances.UnfreezeAsync(subAgencyId, cancellationToken))))
            .WithName("UnfreezeSubAgentAllowance")
            .Produces<AllowanceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // A sub-agent's own view of what it has left. It reads its own row through the widened
        // SELECT policy and can change nothing — see the AddSubAgentNetwork migration.
        app.MapGet("/api/v1/my-allowance", async (
                SubAgentAllowanceService allowances,
                CancellationToken cancellationToken) =>
            {
                var allowance = await allowances.OwnAsync(cancellationToken);

                return allowance is null ? Results.NoContent() : Results.Ok(ToResponse(allowance));
            })
            .WithTags("Sub-agent network")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.WalletView))
            .AddEndpointFilter<SubAgentRefusalFilter>()
            .WithName("GetOwnAllowance")
            .Produces<AllowanceResponse>()
            .Produces(StatusCodes.Status204NoContent);
    }

    // ------------------------------------------------------------------------ reporting

    private static void MapReporting(IEndpointRouteBuilder app)
    {
        // Consolidated figures across the network. A sub-agent calling it sees only its own —
        // that is the service's rule, not a second endpoint.
        app.MapGet("/api/v1/network-performance", async (
                DateTimeOffset? from,
                DateTimeOffset? to,
                ClaimsPrincipal user,
                SubAgentNetworkReport report,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                var end = to ?? clock.GetUtcNow();
                var start = from ?? end.AddDays(-30);
                var withMargin = user.HasClaim(TripsClaimTypes.Permission, PermissionCodes.MarginView);

                var result = await report.RunAsync(start, end, withMargin, cancellationToken);

                // Two shapes, chosen here. Not one shape with nulls: see the class remarks.
                return withMargin
                    ? Results.Ok(new NetworkPerformanceWithMarginResponse(
                        result.From,
                        result.To,
                        result.Currency,
                        result.Orders,
                        result.SalesMinor,
                        result.MarginMinor ?? 0,
                        [.. result.Members.Select(member => new NetworkMemberWithMarginResponse(
                            member.AgencyId,
                            member.Name,
                            member.Status.ToString(),
                            member.IsPrincipal,
                            member.Orders,
                            member.SalesMinor,
                            member.MarginMinor ?? 0,
                            member.AllowanceSpentMinor,
                            member.AllowanceLimitMinor))]))
                    : Results.Ok(new NetworkPerformanceResponse(
                        result.From,
                        result.To,
                        result.Currency,
                        result.Orders,
                        result.SalesMinor,
                        [.. result.Members.Select(member => new NetworkMemberResponse(
                            member.AgencyId,
                            member.Name,
                            member.Status.ToString(),
                            member.IsPrincipal,
                            member.Orders,
                            member.SalesMinor,
                            member.AllowanceSpentMinor,
                            member.AllowanceLimitMinor))]));
            })
            .WithTags("Sub-agent network")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.ReportView))
            .AddEndpointFilter<SubAgentRefusalFilter>()
            .WithName("GetNetworkPerformance")
            .Produces<NetworkPerformanceResponse>()
            .Produces<NetworkPerformanceWithMarginResponse>()
            .ProducesValidationProblem();
    }

    // ------------------------------------------------------------------------ invitations

    private static void MapInvitations(IEndpointRouteBuilder app)
    {
        // Anonymous: whoever holds the link has no account yet. The link itself is the credential.
        var group = app.MapGroup("/api/v1/invitations").WithTags("Sub-agent network");

        group.MapGet("/", async (
                string? token,
                AcceptInvitationHandler handler,
                CancellationToken cancellationToken) =>
            {
                var preview = await handler.PreviewAsync(token ?? string.Empty, cancellationToken);

                return preview is null
                    ? Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "That invitation link is not usable.",
                        detail: "It may have been used already, been withdrawn, or expired. Ask for a new one.")
                    : Results.Ok(new InvitationPreviewResponse(
                        preview.Email, preview.BusinessName, preview.InvitedBy));
            })
            .WithName("PreviewInvitation")
            .Produces<InvitationPreviewResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/accept", async (
                AcceptInvitationRequest request,
                AcceptInvitationHandler handler,
                CancellationToken cancellationToken) =>
            {
                if (request is null)
                {
                    return Results.BadRequest();
                }

                var outcome = await handler.HandleAsync(
                    request.Token,
                    request.FirstName,
                    request.LastName,
                    request.Password,
                    request.PhoneNumber,
                    cancellationToken);

                return outcome switch
                {
                    AcceptInvitationOutcome.Accepted accepted =>
                        Results.Ok(new AcceptInvitationResponse(
                            accepted.UserId, accepted.AgencyId, accepted.Email)),

                    AcceptInvitationOutcome.Invalid invalid =>
                        Results.ValidationProblem(invalid.Errors.ToDictionary(e => e.Key, e => e.Value)),

                    _ => Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "That invitation link is not usable.",
                        detail: "It may have been used already, been withdrawn, or expired. Ask for a new one."),
                };
            })
            .WithName("AcceptInvitation")

            // Anonymous, and a link that turns out to be real ends in an Argon2id hash. Its own policy,
            // so that CPU cannot be spent at the default per-address rate (issue 173).
            .RequireRateLimitPolicy(RateLimitPolicyNames.InvitationAccept)
            .Produces<AcceptInvitationResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // ------------------------------------------------------------------------ helpers

    private static async Task<IResult> NoContentAfter(Task work)
    {
        await work;
        return Results.NoContent();
    }

    private static SubAgentResponse ToResponse(SubAgentSummary summary) =>
        new(
            summary.Id,
            summary.LegalName,
            summary.TradingName,
            summary.Slug,
            summary.Status.ToString(),
            summary.StatusReason,
            summary.CreatedAt,
            summary.HasOpenInvitation,
            summary.ScopeCount,
            summary.DeniedPermissionCount,
            summary.CanSeeMargin,
            summary.AllowanceSpentMinor,
            summary.AllowanceLimitMinor,
            summary.AllowanceCurrency);

    private static AllowanceResponse ToResponse(AllowanceView allowance) =>
        new(
            allowance.SubAgencyId,
            allowance.Currency,
            allowance.SpentMinor,
            allowance.LimitMinor,
            allowance.RemainingMinor,
            allowance.Period.ToString(),
            allowance.Status.ToString(),
            allowance.ResetsAt);

    private static IResult Invalid(string field, string? value, IReadOnlyList<string> allowed) =>
        Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [field] = [$"'{value}' is not one of: {string.Join(", ", allowed)}."],
        });
}
