using TripsAgent.Domain.Identity;

namespace TripsAgent.Domain.Tenancy.SubAgents;

/// <summary>
/// Effective permissions for a sub-agent's user: what its roles give, minus what its principal
/// has taken away.
/// </summary>
/// <remarks>
/// <para>
/// A pure calculation over two lists, with no database and no clock, so the rule that decides
/// whether somebody sees net rates is testable in a unit test and readable in one screen.
/// </para>
/// <para>
/// Subtraction only. See <see cref="PermissionOverride"/> for why there is no grant effect.
/// </para>
/// </remarks>
public static class SubAgentPermissions
{
    /// <summary>
    /// The permissions a user actually has, given the codes their roles grant and the codes their
    /// agency's principal has denied.
    /// </summary>
    /// <remarks>
    /// Order is preserved from <paramref name="granted"/> and duplicates are dropped, so the
    /// result is stable enough to compare in a test and to put on a token.
    /// </remarks>
    public static IReadOnlyList<string> Effective(
        IEnumerable<string> granted,
        IEnumerable<string> denied)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentNullException.ThrowIfNull(denied);

        var blocked = new HashSet<string>(
            denied.Select(Normalise).Where(code => code.Length > 0),
            StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var effective = new List<string>();

        foreach (var code in granted)
        {
            var normalised = Normalise(code);

            if (normalised.Length == 0 || blocked.Contains(normalised) || !seen.Add(normalised))
            {
                continue;
            }

            effective.Add(normalised);
        }

        return effective;
    }

    /// <summary>
    /// Whether a principal may take <paramref name="permissionCode"/> away from a sub-agent.
    /// </summary>
    /// <remarks>
    /// Two refusals, both about keeping the permissions matrix honest rather than about safety:
    /// a code nobody ships is a typo, and a platform-only code is one no agency has ever held, so
    /// denying it would put a row on the matrix that means nothing.
    /// </remarks>
    public static bool CanBeOverridden(string permissionCode)
    {
        var code = Normalise(permissionCode);

        return code.Length > 0
               && PermissionCodes.All.Any(entry => string.Equals(entry.Code, code, StringComparison.Ordinal))
               && !PermissionCodes.PlatformOnly.Contains(code, StringComparer.Ordinal);
    }

    /// <summary>Every code a principal is allowed to deny, in catalogue order.</summary>
    public static IReadOnlyList<string> Overridable { get; } =
    [
        .. PermissionCodes.All
            .Select(entry => entry.Code)
            .Where(code => !PermissionCodes.PlatformOnly.Contains(code, StringComparer.Ordinal)),
    ];

    private static string Normalise(string? code) =>
        (code ?? string.Empty).Trim().ToLowerInvariant();
}
