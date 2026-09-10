using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;

namespace TripsAgent.Application.Identity.Registration;

/// <summary>
/// Sends a fresh verification code. Always "succeeds" from the caller's point of view.
/// </summary>
/// <remarks>
/// Returns nothing on purpose. Whether the address exists, is already verified, or is throttled,
/// the caller sees the same thing — for the same reason registration does.
/// </remarks>
public sealed class ResendVerificationHandler
{
    private readonly IAppDbContext _db;
    private readonly IPlatformScope _platformScope;
    private readonly VerificationCodeIssuer _codes;
    private readonly TimeProvider _clock;

    public ResendVerificationHandler(
        IAppDbContext db,
        IPlatformScope platformScope,
        VerificationCodeIssuer codes,
        TimeProvider clock)
    {
        _db = db;
        _platformScope = platformScope;
        _codes = codes;
        _clock = clock;
    }

    public async Task HandleAsync(ResendVerificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return;
        }

        var email = request.Email.Trim().ToLowerInvariant();

        using var scope = _platformScope.Enter(
            "verification code resend — the account is identified by address before any tenant is known");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null || user.IsEmailVerified)
        {
            return;
        }

        var code = await _codes.PrepareAsync(user, _clock.GetUtcNow(), cancellationToken);

        if (code is null)
        {
            return;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _codes.SendAsync(user, code, cancellationToken);
    }
}
