using TripsAgent.Api.Authorization;
using TripsAgent.Application.Platform;
using TripsAgent.Contracts.Platform;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Platform;

/// <summary>
/// Trips' own back-office accounts and the role each of them holds.
/// </summary>
/// <remarks>
/// Behind <c>platform.user.manage</c>, which only a Super Admin holds: this is the screen that
/// decides who else can suspend a customer, so it is the one that most needs a short list of
/// people who can reach it.
/// </remarks>
public static class PlatformUserEndpoints
{
    public static IEndpointRouteBuilder MapPlatformUserEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/admin/users")
            .WithTags("Back-office users")
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.PlatformUserManage));

        group.MapGet("/", async (PlatformUserService users, CancellationToken cancellationToken) =>
                Results.Ok(await users.ListAsync(cancellationToken)))
            .WithName("PlatformUsers")
            .Produces<IReadOnlyList<PlatformUserResponse>>();

        group.MapGet("/roles", async (PlatformUserService users, CancellationToken cancellationToken) =>
                Results.Ok(await users.RolesAsync(cancellationToken)))
            .WithName("PlatformRoles")
            .Produces<IReadOnlyList<PlatformRoleResponse>>();

        group.MapPost("/", async (
                CreatePlatformUserRequest request,
                PlatformUserService users,
                CancellationToken cancellationToken) =>
                Render(await users.CreateAsync(request, cancellationToken), created: true))
            .WithName("CreatePlatformUser")
            .Produces<PlatformUserResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{userId:guid}/role", async (
                Guid userId,
                ChangePlatformUserRoleRequest request,
                PlatformUserService users,
                CancellationToken cancellationToken) =>
                Render(await users.ChangeRoleAsync(userId, request, cancellationToken)))
            .WithName("ChangePlatformUserRole")
            .Produces<PlatformUserResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{userId:guid}/status", async (
                Guid userId,
                ChangePlatformUserStatusRequest request,
                PlatformUserService users,
                CancellationToken cancellationToken) =>
                Render(await users.ChangeStatusAsync(userId, request, cancellationToken)))
            .WithName("ChangePlatformUserStatus")
            .Produces<PlatformUserResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static IResult Render(PlatformUserOutcome outcome, bool created = false) => outcome switch
    {
        PlatformUserOutcome.Done done => created
            ? Results.Created($"/api/v1/admin/users/{done.User.Id}", done.User)
            : Results.Ok(done.User),

        PlatformUserOutcome.NotFound => Results.NotFound(),

        PlatformUserOutcome.ReasonRequired => Results.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["reason"] =
                [
                    $"Say why, in at least {AgencyLifecycleService.MinReasonLength} characters. "
                    + "It is recorded against your name in the audit log.",
                ],
            }),

        PlatformUserOutcome.Invalid invalid => Results.ValidationProblem(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [invalid.Field] = [invalid.Detail] }),

        // A conflict rather than a validation failure: the request is well formed, and whether
        // that address is already an account is a fact about the world, not about the request.
        PlatformUserOutcome.EmailTaken => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "That email address already has an account.",
            detail: "Every account, agency or back-office, is one address. Check the directory first."),

        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
    };
}
