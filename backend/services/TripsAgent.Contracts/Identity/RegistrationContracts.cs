namespace TripsAgent.Contracts.Identity;

// These records are the API's public shape, and the TypeScript client for the agent console is
// generated from them (`pnpm generate:api`). Renaming a property here is a breaking change for
// the front end — treat them the way you would treat a published API.

/// <summary>A travel business signing up. FRD §2.2 UC-1A RS-1.</summary>
public sealed record RegisterAgentRequest(
    string BusinessName,
    string FirstName,
    string LastName,
    string Email,
    string? PhoneNumber,
    string CountryCode,
    string Password);

/// <summary>The code from the verification email, and the address it was sent to.</summary>
public sealed record VerifyEmailRequest(string Email, string Code);

/// <summary>A request for a fresh verification code.</summary>
public sealed record ResendVerificationRequest(string Email);

/// <summary>
/// The response to a registration or resend. Deliberately identical whether or not the address
/// already has an account — see <c>RegisterAgentHandler</c> for why.
/// </summary>
public sealed record RegistrationAcceptedResponse(string Message);

/// <summary>The response to a successful email verification.</summary>
public sealed record EmailVerifiedResponse(string Message);
