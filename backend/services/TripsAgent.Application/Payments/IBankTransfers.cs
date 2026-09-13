using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Payments;

/// <summary>One bank, as the gateway lists it.</summary>
/// <param name="Code">The gateway's code for it. Gateway-specific and it changes.</param>
/// <param name="Name">What to show a person.</param>
public sealed record BankListing(string Code, string Name);

/// <summary>What the gateway said when asked who owns an account number.</summary>
public enum BankAccountResolution
{
    /// <summary>The bank returned a name.</summary>
    Resolved = 1,

    /// <summary>No such account at that bank. A typo, almost always.</summary>
    NotFound = 2,

    /// <summary>The gateway refused to answer for a reason of its own.</summary>
    Refused = 3,
}

/// <summary>Who the bank says owns an account.</summary>
/// <param name="Outcome">What the answer means.</param>
/// <param name="AccountName">The bank's name for the account. Empty unless resolved.</param>
/// <param name="FailureReason">Why not, when it was not resolved.</param>
public sealed record ResolvedBankAccount(BankAccountResolution Outcome, string AccountName, string? FailureReason)
{
    public bool Resolved => Outcome == BankAccountResolution.Resolved;
}

/// <summary>What a gateway's answer about a transfer means.</summary>
public enum TransferOutcome
{
    /// <summary>Accepted and on its way. Not yet money in a bank account.</summary>
    Queued = 1,

    /// <summary>The money reached the bank.</summary>
    Succeeded = 2,

    /// <summary>The gateway will not send it, and asking again will not change that.</summary>
    Failed = 3,

    /// <summary>It was sent and the bank returned it. Two real movements, not one failure.</summary>
    Reversed = 4,

    /// <summary>
    /// The gateway has not decided, or would not say. Ask again later; <b>never send again</b>.
    /// </summary>
    Unknown = 5,

    /// <summary>
    /// The gateway refused before doing anything: no funds in the platform's own balance, an
    /// unknown recipient, a transfer of this reference already made.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Failed"/> because it is our problem, not the agent's. An agent
    /// whose payout failed because Trips has no float should not be told their bank rejected it.
    /// </remarks>
    Refused = 6,
}

/// <summary>What the gateway says about one transfer.</summary>
/// <param name="Outcome">What its answer means.</param>
/// <param name="Status">Its own status string, kept verbatim for support.</param>
/// <param name="TransferCode">Its handle for the transfer, for the dashboard and for reconciling.</param>
/// <param name="FailureReason">Why, when it went wrong.</param>
public sealed record GatewayTransfer(
    TransferOutcome Outcome,
    string Status,
    string? TransferCode,
    string? FailureReason);

/// <summary>
/// Sending money out, as the application sees it.
/// </summary>
/// <remarks>
/// <para>
/// A port, because the gateway relationship will change and nothing above this may name Paystack.
/// It is also the boundary that keeps the one dangerous rule in one place.
/// </para>
/// <para>
/// <b>Nothing here may be retried automatically.</b>
/// <see cref="InitiateTransferAsync"/> is not idempotent at the gateway in any way we can rely on,
/// and a transfer that times out is an <i>unknown outcome</i>, not a failure. Asking again can put
/// the money in a stranger's bank account twice, and unlike a duplicated ticket there is no
/// supplier to ring — recovery is a legal process. Resolve an unknown outcome with
/// <see cref="GetTransferAsync"/>, which asks the gateway what became of our own reference.
/// This is ADR-0003's rule on a second rail; see docs/adr/0008-never-retry-payout-transfers.md.
/// </para>
/// <para>
/// The implementation must therefore be registered on an HTTP client with <b>no retry policy</b>.
/// </para>
/// </remarks>
public interface IBankTransfers
{
    /// <summary>The gateway's name, as recorded on payouts and reconciliation runs.</summary>
    public string Name { get; }

    /// <summary>
    /// The banks the gateway can send to.
    /// </summary>
    /// <remarks>
    /// Fetched rather than checked into the repository. Bank codes are the gateway's, and Nigerian
    /// banks merge, rebrand and are licensed every year — a hard-coded list is a list that is
    /// wrong by the time somebody needs it.
    /// </remarks>
    public Task<IReadOnlyList<BankListing>> ListBanksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the bank who owns an account number. A read; safe to call again.
    /// </summary>
    public Task<ResolvedBankAccount> ResolveAccountAsync(
        string accountNumber,
        string bankCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a destination with the gateway and returns its handle.
    /// </summary>
    /// <remarks>
    /// Created once per bank account and kept, so transfers address a recipient code and never a
    /// raw account number.
    /// </remarks>
    public Task<string> CreateRecipientAsync(
        string accountNumber,
        string bankCode,
        string accountName,
        string currency,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What the platform's own balance at the gateway is, in minor units.
    /// </summary>
    /// <remarks>
    /// Checked before a transfer so an empty float raises an operational alert instead of failing
    /// an agent's payout as though they had done something wrong. Null when the gateway will not
    /// say, which is not a reason to stop.
    /// </remarks>
    public Task<Money?> GetBalanceAsync(string currency, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends money. <b>Call this at most once per payout, ever.</b>
    /// </summary>
    /// <param name="reference">
    /// Our own reference for the payout, sent as the gateway's transfer reference so a second
    /// initiation is refused by the gateway rather than duplicated by it. That is the backstop,
    /// not the plan.
    /// </param>
    /// <param name="recipientCode">The gateway's handle for the destination.</param>
    /// <param name="amount">Minor units.</param>
    /// <param name="currency">ISO 4217.</param>
    /// <param name="reason">What the recipient sees on their statement.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Throws <see cref="PaymentGatewayUnavailableException"/> when the gateway could not be
    /// reached or did not answer in time. <b>That is an unknown outcome.</b> The caller must record
    /// it as unknown and resolve it with <see cref="GetTransferAsync"/> — never by calling this
    /// again.
    /// </remarks>
    public Task<GatewayTransfer> InitiateTransferAsync(
        string reference,
        string recipientCode,
        Money amount,
        string currency,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks what became of a transfer, by our own reference.
    /// </summary>
    /// <remarks>
    /// The only way an unknown outcome is ever resolved. A read, so it may be retried freely — and
    /// a reference the gateway has never heard of comes back as
    /// <see cref="TransferOutcome.Unknown"/> rather than as a failure, because "we have no record"
    /// and "it did not happen" are not the same sentence and only one of them is safe to act on.
    /// </remarks>
    public Task<GatewayTransfer> GetTransferAsync(string reference, CancellationToken cancellationToken = default);
}
