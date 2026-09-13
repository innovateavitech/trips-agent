using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Identity.Registration;
using TripsAgent.Application.Persistence;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Tenancy.SubAgents;

/// <summary>What an invitation looks like to whoever is holding the link.</summary>
/// <param name="Email">The address it was sent to. The account is created with it, and it cannot be changed.</param>
/// <param name="BusinessName">The sub-agency's name, so the page can say what is being joined.</param>
/// <param name="InvitedBy">The principal's trading name. Never ours — see build-plan decision 6.</param>
public sealed record InvitationPreview(string Email, string BusinessName, string InvitedBy);

/// <summary>What happened when somebody tried to use an invitation.</summary>
public abstract record AcceptInvitationOutcome
{
    private AcceptInvitationOutcome()
    {
    }

    /// <summary>
    /// The account exists. It signs in once the code just sent to its address has been entered,
    /// exactly as a self-registered account does (issue 170).
    /// </summary>
    public sealed record Accepted(Guid UserId, Guid AgencyId, string Email) : AcceptInvitationOutcome;

    /// <summary>The link is wrong, used, revoked or expired. Deliberately one case, not four.</summary>
    public sealed record NotUsable : AcceptInvitationOutcome;

    /// <summary>The details are wrong — a weak password, a missing name.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : AcceptInvitationOutcome;
}

/// <summary>
/// Turns an invitation link into the sub-agency's first user.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single use, and only ever forwards.</b> The token is stored as a keyed hash and looked up by
/// it, the row is marked accepted in the same transaction that creates the user, and a second
/// attempt finds a closed invitation. Accepting twice therefore cannot create two owners.
/// </para>
/// <para>
/// <b>Anonymous by necessity.</b> Whoever clicks the link has no account yet, so there is no
/// tenant and the query filters would hide the invitation from the very request that needs it.
/// This is one of the handful of places <see cref="IPlatformScope"/> is the right answer, for the
/// same reason registration uses it: the lookup is by a secret the caller already holds, and it
/// says so in the log.
/// </para>
/// <para>
/// <b>Holding the link does not prove the address.</b> The principal sees the link too — its console
/// shows it once, so it can be passed on when an email goes astray — so accepting proves only that
/// somebody had the link. Taking it as proof of the inbox let a principal accept its own invitation
/// for somebody else's address and hold an account marked verified that nobody there ever saw; and
/// since an address is one account platform-wide, its owner could then never register it (issue 170).
/// So the account is created unverified, the six-digit code registration sends goes to the address,
/// and the account signs in once that code has come back — the same rule as a self-registered
/// account, through the same <see cref="VerificationCodeIssuer"/>.
/// </para>
/// </remarks>
public sealed class AcceptInvitationHandler
{
    private readonly IAppDbContext _db;
    private readonly ITokenHasher _tokens;
    private readonly IPasswordHasher _passwords;
    private readonly IPlatformScope _platformScope;
    private readonly ITransactionRunner _transactions;
    private readonly VerificationCodeIssuer _codes;
    private readonly TimeProvider _clock;

    public AcceptInvitationHandler(
        IAppDbContext db,
        ITokenHasher tokens,
        IPasswordHasher passwords,
        IPlatformScope platformScope,
        ITransactionRunner transactions,
        VerificationCodeIssuer codes,
        TimeProvider clock)
    {
        _db = db;
        _tokens = tokens;
        _passwords = passwords;
        _platformScope = platformScope;
        _transactions = transactions;
        _codes = codes;
        _clock = clock;
    }

    /// <summary>What the accept page shows before anyone types anything.</summary>
    public async Task<InvitationPreview?> PreviewAsync(string token, CancellationToken cancellationToken = default)
    {
        using var scope = _platformScope.Enter(
            "sub-agent invitation — the invitee has no account yet, so there is no tenant to scope this to");

        var invitation = await OpenInvitationAsync(token, cancellationToken);

        if (invitation is null)
        {
            return null;
        }

        var agency = await _db.Agencies
            .AsNoTracking()
            .Where(candidate => candidate.Id == invitation.AgencyId)
            .Select(candidate => new { candidate.LegalName, candidate.TradingName, candidate.ParentAgencyId })
            .SingleOrDefaultAsync(cancellationToken);

        if (agency is null)
        {
            return null;
        }

        var principal = await _db.Agencies
            .AsNoTracking()
            .Where(candidate => candidate.Id == agency.ParentAgencyId)
            .Select(candidate => candidate.TradingName ?? candidate.LegalName)
            .SingleOrDefaultAsync(cancellationToken);

        return new InvitationPreview(
            invitation.Email,
            agency.TradingName ?? agency.LegalName,
            principal ?? agency.TradingName ?? agency.LegalName);
    }

    /// <summary>Creates the account the invitation was for, unverified, and sends its address the code.</summary>
    public async Task<AcceptInvitationOutcome> HandleAsync(
        string token,
        string firstName,
        string lastName,
        string password,
        string? phoneNumber = null,
        CancellationToken cancellationToken = default)
    {
        var errors = Validate(firstName, lastName, password);

        if (errors.Count > 0)
        {
            return new AcceptInvitationOutcome.Invalid(errors);
        }

        // Hashed before the lookup, so the response time does not say whether the token was real.
        var passwordHash = _passwords.Hash(password);

        using var scope = _platformScope.Enter(
            "sub-agent invitation — the invitee has no account yet, so there is no tenant to scope this to");

        var invitation = await OpenInvitationAsync(token, cancellationToken);

        if (invitation?.AgencyId is not { } agencyId)
        {
            return new AcceptInvitationOutcome.NotUsable();
        }

        var agency = await _db.Agencies
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == agencyId, cancellationToken);

        // A sub-agent whose principal ended it before anyone accepted. The invitation is revoked
        // when that happens, so this is only reachable if the two raced.
        if (agency is null || !AgencyAccess.CanSignIn(agency.Status))
        {
            return new AcceptInvitationOutcome.NotUsable();
        }

        // An address is one account across the whole platform, so this cannot become a second one.
        if (await _db.Users.AnyAsync(user => user.Email == invitation.Email, cancellationToken))
        {
            return new AcceptInvitationOutcome.NotUsable();
        }

        var now = _clock.GetUtcNow();

        var created = await _transactions.RunAsync(
            async ct =>
            {
                var account = User.ForAgency(agencyId, invitation.Email, passwordHash, firstName, lastName);
                account.SetPhoneNumber(phoneNumber);

                // Deliberately not verified: see the class remarks. Nobody at the address has done
                // anything yet — the code below is how they will.
                _db.Users.Add(account);
                _db.UserRoles.Add(UserRole.Grant(account.Id, invitation.RoleId, agencyId));

                invitation.MarkAccepted(now);

                // Staged with the account, so a code that reaches the inbox always exists in the database.
                var issued = await _codes.PrepareAsync(account, now, ct);

                await _db.SaveChangesAsync(ct);

                return new CreatedAccount(account, issued);
            },
            cancellationToken);

        // After the commit, never inside it: an email cannot be taken back if the save fails.
        if (created.Code is not null)
        {
            await _codes.SendAsync(created.User, created.Code, cancellationToken);
        }

        return new AcceptInvitationOutcome.Accepted(created.User.Id, agencyId, invitation.Email);
    }

    private async Task<UserInvitation?> OpenInvitationAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = _tokens.Hash(token);
        var now = _clock.GetUtcNow();

        return await _db.UserInvitations
            .Where(invitation => invitation.TokenHash == hash && invitation.AgencyId != null)
            .SingleOrDefaultAsync(cancellationToken) is { } found && found.IsOpen(now)
            ? found
            : null;
    }

    private static Dictionary<string, string[]> Validate(string firstName, string lastName, string password)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(firstName))
        {
            errors["firstName"] = ["First name is required."];
        }

        if (string.IsNullOrWhiteSpace(lastName))
        {
            errors["lastName"] = ["Last name is required."];
        }

        var passwordProblems = PasswordPolicy.Validate(password);

        if (passwordProblems.Count > 0)
        {
            errors["password"] = [.. passwordProblems];
        }

        return errors;
    }

    /// <summary>The account the transaction made, and the code to email once it has committed.</summary>
    private sealed record CreatedAccount(User User, string? Code);
}
