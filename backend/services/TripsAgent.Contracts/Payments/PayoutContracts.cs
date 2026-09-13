namespace TripsAgent.Contracts.Payments;

/// <summary>A bank the gateway can send money to.</summary>
public sealed record BankResponse(string Code, string Name);

/// <summary>An account to add. The name typed is kept only to show a mismatch; the bank's name is what is stored.</summary>
public sealed record AddBankAccountRequest(string BankCode, string AccountNumber, string AccountName);

/// <summary>A payout destination, as the console shows it. The number is only ever the last four digits.</summary>
/// <param name="Status">PendingVerification, Verified, Rejected or Removed.</param>
/// <param name="AccountNameResolved">What the bank calls the account. Null until the bank answered.</param>
/// <param name="UsableFrom">When a newly added account may receive its first withdrawal.</param>
public sealed record BankAccountResponse(
    Guid Id,
    string BankName,
    string MaskedNumber,
    string? AccountNameResolved,
    string AccountNameProvided,
    string Status,
    string? RejectionReason,
    bool IsDefault,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? UsableFrom,
    string Currency);

/// <summary>What happened when an account was added.</summary>
/// <param name="NameMatchesBusiness">False when the bank's name looks nothing like the agency's own.</param>
public sealed record AddBankAccountResponse(
    Guid BankAccountId,
    string AccountName,
    bool NameMatchesBusiness,
    DateTimeOffset UsableFrom);

/// <summary>
/// What may be withdrawn, and the three numbers between the balance and that.
/// </summary>
/// <param name="PendingSettlementMinor">Card money paid in too recently to have reached the platform's bank.</param>
/// <param name="WithdrawableMinor">Available less pending settlement. Never negative.</param>
public sealed record WithdrawableBalanceResponse(
    long BalanceMinor,
    long ReservedMinor,
    long AvailableMinor,
    long PendingSettlementMinor,
    long WithdrawableMinor,
    long MinimumPayoutMinor,
    long DailyCapMinor,
    int SettlementWindowDays,
    string Currency);

/// <summary>A withdrawal to ask for, in minor units. No account means the default one.</summary>
public sealed record RequestPayoutRequest(long AmountMinor, Guid? BankAccountId);

/// <summary>A withdrawal was recorded and waits for approval.</summary>
public sealed record RequestPayoutResponse(Guid PayoutId, string Reference, long AmountMinor);

/// <summary>One withdrawal.</summary>
/// <param name="Status">Requested, Approved, Rejected, Sending, OutcomeUnknown, Paid, Failed or Reversed.</param>
public sealed record PayoutResponse(
    Guid Id,
    string Reference,
    long AmountMinor,
    string Currency,
    string Status,
    string BankName,
    string MaskedNumber,
    DateTimeOffset RequestedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? CompletedAt,
    string? RejectionReason,
    string? FailureReason);

/// <summary>A withdrawal in the Finance approval queue.</summary>
public sealed record PayoutApprovalItemResponse(
    Guid Id,
    Guid AgencyId,
    string AgencyName,
    string Reference,
    long AmountMinor,
    string Currency,
    string Status,
    string BankName,
    string MaskedNumber,
    string? AccountNameResolved,
    Guid RequestedByUserId,
    DateTimeOffset RequestedAt);

/// <summary>Why Finance refused a withdrawal. Shown to the agency.</summary>
public sealed record RejectPayoutRequest(string Reason);

/// <summary>One chargeback.</summary>
/// <param name="Status">Open, EvidenceSubmitted, Expired, Won or Lost.</param>
/// <param name="HoldOutcome">None, Held, Uncovered or Settled.</param>
public sealed record DisputeResponse(
    Guid Id,
    Guid AgencyId,
    string PaymentReference,
    Guid? OrderId,
    long AmountMinor,
    string Currency,
    string? Category,
    string? Reason,
    string Status,
    string HoldOutcome,
    DateTimeOffset OpenedAt,
    DateTimeOffset EvidenceDueAt,
    DateTimeOffset? EvidenceSubmittedAt,
    DateTimeOffset? ResolvedAt,
    string? Resolution,
    bool AcceptsEvidence,
    DisputeEvidenceDefaults? EvidenceDefaults);

/// <summary>What the evidence form starts with, taken from the order's customer.</summary>
public sealed record DisputeEvidenceDefaults(
    string? CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string? ServiceDetails);

/// <summary>Evidence contesting a chargeback.</summary>
public sealed record SubmitDisputeEvidenceRequest(
    string CustomerName,
    string CustomerEmail,
    string CustomerPhone,
    string ServiceDetails,
    DateOnly? DeliveryDate,
    string? Note,
    IReadOnlyList<Guid>? AssetIds);

/// <summary>One day's reconciliation.</summary>
public sealed record ReconciliationRunResponse(
    Guid Id,
    DateOnly BusinessDate,
    string Gateway,
    string Status,
    int RecordsExamined,
    int RecordsMatched,
    int ExceptionsRaised,
    long GatewayGrossMinor,
    long GatewayFeesMinor,
    long GatewayNetMinor,
    long LedgerGrossMinor,
    long DifferenceMinor,
    string Currency,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason);

/// <summary>One discrepancy for a person to work through.</summary>
public sealed record ReconciliationExceptionResponse(
    Guid Id,
    string Check,
    string Severity,
    string Status,
    string Subject,
    string Detail,
    long ExpectedMinor,
    long ActualMinor,
    long DifferenceMinor,
    Guid? AgencyId,
    Guid? ReconciliationRunId,
    int TimesSeen,
    DateTimeOffset DetectedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote);

/// <summary>Closing an exception: "Resolve" or "WriteOff", with a reason either way.</summary>
public sealed record CloseReconciliationExceptionRequest(string Closure, string Note);
