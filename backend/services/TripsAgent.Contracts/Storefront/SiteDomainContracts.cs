namespace TripsAgent.Contracts.Storefront;

/// <summary>Connects one of the agency's own hostnames to its site.</summary>
/// <param name="Hostname">For example <c>www.yourbusiness.com</c>.</param>
public sealed record AddSiteDomainRequest(string Hostname);

/// <summary>One hostname the site answers on, and where it has got to.</summary>
/// <param name="Type"><c>Subdomain</c> (the free address) or <c>Custom</c>.</param>
/// <param name="VerificationStatus"><c>Pending</c>, <c>Verified</c> or <c>Abandoned</c>.</param>
/// <param name="SslStatus"><c>None</c>, <c>Pending</c>, <c>Issued</c>, <c>Failed</c> or <c>Expired</c> — separate from verification, because a host can be verified and not yet secured.</param>
/// <param name="NextCheckAt">When we look for the records next, while they are pending.</param>
/// <param name="DnsRecords">The records to create at the registrar, for a custom hostname.</param>
/// <param name="RecentChecks">What the latest checks looked for and found, newest first.</param>
public sealed record SiteDomainResponse(
    Guid Id,
    string Hostname,
    string Type,
    bool IsPrimary,
    string VerificationStatus,
    string SslStatus,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? NextCheckAt,
    DateTimeOffset? SslExpiresAt,
    string? SslLastError,
    IReadOnlyList<SiteDnsRecordResponse> DnsRecords,
    IReadOnlyList<SiteDomainCheckResponse> RecentChecks,
    bool CanRemove,
    bool CanMakePrimary,
    bool CanCheckNow);

/// <summary>One DNS record the agent must create, ready to copy.</summary>
/// <param name="RecordType"><c>TXT</c> or <c>CNAME</c>.</param>
/// <param name="Name">The full record name.</param>
/// <param name="HostLabel">
/// The same name without the domain on the end — what most registrars' "Host" field wants. Typing the
/// full name there is the commonest way a record ends up at <c>name.example.com.example.com</c>.
/// </param>
/// <param name="Purpose">Why the record is needed, in a sentence.</param>
public sealed record SiteDnsRecordResponse(string RecordType, string Name, string HostLabel, string Value, string Purpose);

/// <summary>One look for one record.</summary>
/// <param name="Outcome"><c>Match</c>, <c>Mismatch</c>, <c>NotFound</c>, <c>Timeout</c>, <c>ServerFailure</c> or <c>Error</c>.</param>
public sealed record SiteDomainCheckResponse(
    DateTimeOffset CheckedAt,
    string RecordType,
    string RecordName,
    string Expected,
    IReadOnlyList<string> Observed,
    string Outcome,
    string Resolver,
    string? Detail);
