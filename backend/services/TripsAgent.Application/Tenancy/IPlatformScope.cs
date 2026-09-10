namespace TripsAgent.Application.Tenancy;

/// <summary>
/// The one sanctioned way to read across agencies.
/// </summary>
/// <remarks>
/// <para>
/// Almost nothing needs this. Platform-admin reporting does — the KYB review queue, platform GMV,
/// the agent leaderboard — and everything else does not. CLAUDE.md rule 3 puts the count at two
/// or three legitimate uses in the whole codebase.
/// </para>
/// <para>
/// It is deliberately awkward: you have to enter it explicitly, you have to say why in words, and
/// the reason is written to the log with the acting user. That record is the point. An unexplained
/// cross-tenant read is indistinguishable from a leak after the fact, so there is no overload
/// without a reason.
/// </para>
/// <example>
/// <code>
/// using (platformScope.Enter("KYB review queue — lists submissions from every agency"))
/// {
///     var pending = await db.KybSubmissions.Where(s => s.Status == Submitted).ToListAsync();
/// }
/// </code>
/// </example>
/// </remarks>
public interface IPlatformScope
{
    /// <summary>True while a scope is open. The query filters read this.</summary>
    public bool IsActive { get; }

    /// <summary>
    /// Opens a cross-tenant scope until the returned handle is disposed.
    /// </summary>
    /// <param name="reason">
    /// Why this read has to cross agencies, in a sentence a reviewer reading the audit log in six
    /// months will understand. Required.
    /// </param>
    public IDisposable Enter(string reason);
}
