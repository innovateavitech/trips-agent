namespace TripsAgent.Contracts.Identity;

/// <summary>A request for a password reset link.</summary>
public sealed record ForgotPasswordRequest(string Email);

/// <summary>A new password, and the token from the emailed link.</summary>
public sealed record ResetPasswordRequest(string Token, string NewPassword);
