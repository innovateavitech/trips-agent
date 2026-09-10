using TripsAgent.Application.Identity.Registration;
using TripsAgent.Contracts.Identity;

namespace TripsAgent.Api.Identity;

/// <summary>Anonymous endpoints a travel business uses to sign up and confirm its email.</summary>
/// <remarks>
/// Deliberately thin. Every decision — validation, enumeration safety, throttling — lives in the
/// handlers, where it can be unit-tested without a web server. These only translate outcomes into
/// HTTP.
/// </remarks>
public static class RegistrationEndpoints
{
    /// <summary>
    /// The one message both a new and an already-registered address receive. Wording matters:
    /// it must be true in both cases, so it promises nothing about whether an email was sent.
    /// </summary>
    public const string AcceptedMessage =
        "If that email address can be registered, we've sent a verification code to it. "
        + "Check your inbox — the code expires in 15 minutes.";

    public static IEndpointRouteBuilder MapRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/auth").WithTags("Registration");

        group.MapPost("/register", async (
                RegisterAgentRequest request,
                RegisterAgentHandler handler,
                CancellationToken cancellationToken) =>
            {
                var outcome = await handler.HandleAsync(request, cancellationToken);

                return outcome switch
                {
                    // 202, not 201: nothing is usable yet, and a 201 with a Location header would
                    // itself reveal that a new account was created.
                    RegistrationOutcome.Accepted =>
                        Results.Accepted(value: new RegistrationAcceptedResponse(AcceptedMessage)),

                    RegistrationOutcome.Invalid invalid =>
                        Results.ValidationProblem(invalid.Errors.ToDictionary(e => e.Key, e => e.Value)),

                    _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
                };
            })
            .WithName("RegisterAgent")
            .Produces<RegistrationAcceptedResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem();

        group.MapPost("/verify-email", async (
                VerifyEmailRequest request,
                VerifyEmailHandler handler,
                CancellationToken cancellationToken) =>
            {
                var outcome = await handler.HandleAsync(request, cancellationToken);

                return outcome == VerifyEmailOutcome.Verified
                    ? Results.Ok(new EmailVerifiedResponse("Your email address is verified."))
                    : Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "That code is invalid or has expired.",
                        detail: "Check the code, or ask for a new one.");
            })
            .WithName("VerifyEmail")
            .Produces<EmailVerifiedResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/resend-verification", async (
                ResendVerificationRequest request,
                ResendVerificationHandler handler,
                CancellationToken cancellationToken) =>
            {
                await handler.HandleAsync(request, cancellationToken);

                // Identical whatever happened — see ResendVerificationHandler.
                return Results.Accepted(value: new RegistrationAcceptedResponse(AcceptedMessage));
            })
            .WithName("ResendVerification")
            .Produces<RegistrationAcceptedResponse>(StatusCodes.Status202Accepted);

        return app;
    }
}
