using Microsoft.EntityFrameworkCore;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Contracts.Identity;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;

namespace TripsAgent.Application.Identity.Registration;

/// <summary>What happened to a registration request.</summary>
public abstract record RegistrationOutcome
{
    private RegistrationOutcome()
    {
    }

    /// <summary>
    /// The request was well-formed. Returned whether or not the address already had an account.
    /// </summary>
    public sealed record Accepted : RegistrationOutcome;

    /// <summary>
    /// The request itself was malformed — a weak password, a missing field. Safe to report in
    /// detail: none of it depends on whether the address is already registered.
    /// </summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : RegistrationOutcome;
}

/// <summary>
/// Signs a travel business up: creates the agency in <c>pending_verification</c>, its owner, and
/// emails a verification code. FRD §2.2 UC-1A RS-1 to RS-3.
/// </summary>
/// <remarks>
/// <para>
/// <b>No account enumeration.</b> Registering an address that already exists returns exactly the
/// same response as registering a new one. Otherwise the signup form becomes a free oracle for
/// "does this person have an account with us?" — a list of our customers, one guess at a time.
/// </para>
/// <para>
/// That has to hold for <i>timing</i> as well as content. The password is hashed before anything
/// is looked up, on every path, because Argon2id is deliberately slow: if only new registrations
/// paid for it, the response time alone would reveal which addresses exist.
/// </para>
/// <para>
/// An existing <i>unverified</i> account gets a fresh code, which is what someone who lost the
/// first email needs. An existing <i>verified</i> account gets nothing — sending it a "you already
/// have an account" email would let anyone spam a stranger's inbox through our signup form.
/// </para>
/// </remarks>
public sealed class RegisterAgentHandler
{
    private readonly IAppDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IPlatformScope _platformScope;
    private readonly VerificationCodeIssuer _codes;
    private readonly TimeProvider _clock;

    public RegisterAgentHandler(
        IAppDbContext db,
        IPasswordHasher passwordHasher,
        IPlatformScope platformScope,
        VerificationCodeIssuer codes,
        TimeProvider clock)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _platformScope = platformScope;
        _codes = codes;
        _clock = clock;
    }

    public async Task<RegistrationOutcome> HandleAsync(RegisterAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return new RegistrationOutcome.Invalid(errors);
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var now = _clock.GetUtcNow();

        // Before any lookup, on every path — see the class remarks on timing.
        var passwordHash = _passwordHasher.Hash(request.Password);

        // Registration happens before there is a tenant, and an email address is unique across
        // every agency, so the existence check has to see all of them.
        using var scope = _platformScope.Enter(
            "agent registration — email addresses are unique across every agency");

        var existing = await _db.Users.FirstOrDefaultAsync(user => user.Email == email, cancellationToken);

        if (existing is not null)
        {
            await ResendIfUnverifiedAsync(existing, now, cancellationToken);
            return new RegistrationOutcome.Accepted();
        }

        var ownerRole = await _db.Roles.FirstOrDefaultAsync(
            role => role.AgencyId == null && role.Name == Role.SystemRoles.Owner,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The system Owner role does not exist, so a new agency has nobody to own it. "
                + "Reference data has not been loaded — run the migrate command.");

        var market = SupportedMarkets.ByCountry[request.CountryCode.Trim()];

        var agency = Agency.RegisterPrincipal(
            legalName: request.BusinessName.Trim(),
            slug: await UniqueSlugAsync(request.BusinessName, cancellationToken),
            countryCode: request.CountryCode.Trim(),
            baseCurrency: market.Currency,
            timezone: market.Timezone);

        var owner = User.ForAgency(
            agency.Id,
            email,
            passwordHash,
            request.FirstName,
            request.LastName);

        owner.SetPhoneNumber(request.PhoneNumber);

        _db.Agencies.Add(agency);
        _db.AgencySettings.Add(AgencySettings.CreateDefault(agency));
        _db.AgencyBranding.Add(AgencyBranding.CreateDefault(agency));
        _db.Users.Add(owner);
        _db.UserRoles.Add(UserRole.Grant(owner.Id, ownerRole.Id, agency.Id));

        // A brand-new address cannot be throttled, but the issuer is asked all the same so there
        // is exactly one code path that decides.
        var code = await _codes.PrepareAsync(owner, now, cancellationToken);

        // Agency, settings, branding, owner, role grant and code commit together, or not at all.
        await _db.SaveChangesAsync(cancellationToken);

        if (code is not null)
        {
            await _codes.SendAsync(owner, code, cancellationToken);
        }

        return new RegistrationOutcome.Accepted();
    }

    private async Task ResendIfUnverifiedAsync(User existing, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (existing.IsEmailVerified)
        {
            return;
        }

        var code = await _codes.PrepareAsync(existing, now, cancellationToken);

        if (code is null)
        {
            return;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _codes.SendAsync(existing, code, cancellationToken);
    }

    /// <summary>
    /// Derives a URL-safe slug from the business name, adding a number when it is taken.
    /// </summary>
    /// <remarks>
    /// The unique index is still the real guarantee: two agencies registering "Lagos Travel" in
    /// the same instant could both see the slug as free. That race ends in a failed insert rather
    /// than a duplicate, and the slug is editable during onboarding anyway.
    /// </remarks>
    private async Task<string> UniqueSlugAsync(string businessName, CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(businessName);
        var candidate = baseSlug;

        for (var suffix = 2; await _db.Agencies.AnyAsync(a => a.Slug == candidate, cancellationToken); suffix++)
        {
            candidate = $"{baseSlug}-{suffix}";
        }

        return candidate;
    }

    private static string Slugify(string value)
    {
        var builder = new System.Text.StringBuilder();
        var previousWasHyphen = true;   // suppresses a leading hyphen

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasHyphen = false;
            }
            else if (!previousWasHyphen)
            {
                builder.Append('-');
                previousWasHyphen = true;
            }
        }

        var slug = builder.ToString().TrimEnd('-');

        // Leaves room for a "-NN" suffix inside the 63-character column.
        if (slug.Length > 50)
        {
            slug = slug[..50].TrimEnd('-');
        }

        return slug.Length == 0 ? "agency" : slug;
    }

    private static Dictionary<string, string[]> Validate(RegisterAgentRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        void Require(string? value, string field, string message)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors[field] = [message];
            }
        }

        Require(request.BusinessName, nameof(request.BusinessName), "Business name is required.");
        Require(request.FirstName, nameof(request.FirstName), "First name is required.");
        Require(request.LastName, nameof(request.LastName), "Last name is required.");
        Require(request.Email, nameof(request.Email), "Email is required.");
        Require(request.CountryCode, nameof(request.CountryCode), "Country is required.");

        if (!string.IsNullOrWhiteSpace(request.Email)
            && (!request.Email.Contains('@', StringComparison.Ordinal) || request.Email.Trim().Length < 3))
        {
            errors[nameof(request.Email)] = ["Enter a valid email address."];
        }

        if (!string.IsNullOrWhiteSpace(request.CountryCode)
            && !SupportedMarkets.ByCountry.ContainsKey(request.CountryCode.Trim()))
        {
            errors[nameof(request.CountryCode)] = ["We can't take registrations from that country yet."];
        }

        var passwordProblems = PasswordPolicy.Validate(request.Password);
        if (passwordProblems.Count > 0)
        {
            errors[nameof(request.Password)] = [.. passwordProblems];
        }

        return errors;
    }
}
