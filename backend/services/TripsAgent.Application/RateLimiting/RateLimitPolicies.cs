namespace TripsAgent.Application.RateLimiting;

/// <summary>
/// The rate limit policies, by name. An endpoint names one with <c>RequireRateLimitPolicy</c>; every
/// other request is counted against <see cref="Default"/>.
/// </summary>
/// <remarks>
/// The names double as configuration keys — <c>RateLimiting__Policies__Login__PermitLimit</c> — so
/// renaming one silently orphans whatever an environment had configured for it.
/// </remarks>
public static class RateLimitPolicyNames
{
    /// <summary>Every request that names no other policy.</summary>
    public const string Default = "Default";

    /// <summary>Signing in. Every attempt is a password guess.</summary>
    public const string Login = "Login";

    /// <summary>Creating an agency account. Each one creates rows and sends an email.</summary>
    public const string Registration = "Registration";

    /// <summary>Asking for another verification code. Each one sends an email.</summary>
    public const string OtpResend = "OtpResend";

    /// <summary>Asking for a password reset link. Each one sends an email.</summary>
    public const string ForgotPassword = "ForgotPassword";

    /// <summary>Flight and bus search. Each one can cost a call to the supplier.</summary>
    public const string Search = "Search";

    /// <summary>
    /// The ceiling on one agency's traffic, across all of its users and every endpoint.
    /// </summary>
    /// <remarks>
    /// Not something an endpoint opts into: it is counted on top of whichever policy applies, for
    /// every signed-in request that carries an agency. It is the tenant filter applied to capacity
    /// instead of data — one agency's runaway script must not slow every other agency down.
    /// </remarks>
    public const string Agency = "Agency";

    /// <summary>Every policy, in the order configuration documents them.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Default, Login, Registration, OtpResend, ForgotPassword, Search, Agency];

    /// <summary>True for a policy an endpoint may name. <see cref="Agency"/> is not one.</summary>
    public static bool IsEndpointPolicy(string? name) =>
        name is not null && name != Agency && All.Contains(name, StringComparer.Ordinal);
}

/// <summary>At most <see cref="PermitLimit"/> requests in each <see cref="Window"/>.</summary>
/// <remarks>
/// A fixed window that starts with the first request, timed by Redis. The known cost of a fixed
/// window: a caller can spend a whole limit at the end of one window and another at the start of
/// the next, so a burst of up to twice the limit is possible across the boundary. Accepted — it is
/// simple to reason about, and the limits below leave room for it.
/// </remarks>
public sealed record RateLimitRule(int PermitLimit, TimeSpan Window);

/// <summary>Whether rate limiting is on, and the rule for each policy.</summary>
public sealed class RateLimitSettings
{
    public RateLimitSettings(bool enabled, IReadOnlyDictionary<string, RateLimitRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var missing = RateLimitPolicyNames.All.Where(name => !rules.ContainsKey(name)).ToList();

        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"Every rate limit policy needs a rule. Missing: {string.Join(", ", missing)}.", nameof(rules));
        }

        Enabled = enabled;
        Rules = rules;
    }

    /// <summary>
    /// The limits used when configuration says nothing. Every one can be overridden per
    /// environment; the reasoning for each number is beside it.
    /// </summary>
    public static IReadOnlyDictionary<string, RateLimitRule> Defaults { get; } =
        new Dictionary<string, RateLimitRule>(StringComparer.Ordinal)
        {
            // Five a second, sustained, per signed-in user or per anonymous address. A console
            // screen makes a handful of calls; a script scraping as one user stands out.
            [RateLimitPolicyNames.Default] = new(300, TimeSpan.FromMinutes(1)),

            // Per address. A travel office behind one NAT address signing in at nine o'clock gets
            // through; a credential-stuffing run from one address manages four guesses a minute.
            // Guessing at one account from many addresses is account lockout's job (#15).
            [RateLimitPolicyNames.Login] = new(20, TimeSpan.FromMinutes(5)),

            // Each one creates rows and sends an email. Nobody registers ten agencies in an hour.
            [RateLimitPolicyNames.Registration] = new(10, TimeSpan.FromHours(1)),

            // Each one sends an email, on our sending reputation. A person needs one or two.
            [RateLimitPolicyNames.OtpResend] = new(5, TimeSpan.FromMinutes(15)),
            [RateLimitPolicyNames.ForgotPassword] = new(5, TimeSpan.FromMinutes(15)),

            // Per user. The one request that can cost us a supplier call, so the tightest of the
            // signed-in policies: one search every two seconds, sustained, is still a busy agent.
            [RateLimitPolicyNames.Search] = new(30, TimeSpan.FromMinutes(1)),

            // Per agency, across all its users: four busy users' worth of the default.
            [RateLimitPolicyNames.Agency] = new(1200, TimeSpan.FromMinutes(1)),
        };

    /// <summary>False only where configuration switched it off explicitly.</summary>
    public bool Enabled { get; }

    /// <summary>The rule for each policy in <see cref="RateLimitPolicyNames.All"/>.</summary>
    public IReadOnlyDictionary<string, RateLimitRule> Rules { get; }

    /// <summary>The rule for <paramref name="policyName"/>.</summary>
    public RateLimitRule RuleFor(string policyName) =>
        Rules.TryGetValue(policyName, out var rule)
            ? rule
            : throw new ArgumentException($"There is no rate limit policy named '{policyName}'.", nameof(policyName));
}
