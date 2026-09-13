namespace TripsAgent.Domain.Analytics;

/// <summary>How far a report reaches.</summary>
/// <remarks>
/// The distinction that decides almost everything about a report: whether it stays inside one
/// agency, or crosses them. A <see cref="Platform"/> report is read through
/// <c>IPlatformScope.Enter</c>, needs a platform permission, always runs asynchronously, and is
/// the reason every export is logged.
/// </remarks>
public enum ReportScope
{
    /// <summary>One agency's own rows, read under the ordinary tenant filter.</summary>
    Agency = 1,

    /// <summary>Every agency at once. Platform staff only.</summary>
    Platform = 2,
}

/// <summary>The file a report is written as.</summary>
/// <remarks>
/// CSV and nothing else for the MVP — see "what the MVP leaves out" in the build plan. The enum
/// exists so adding XLSX later is a new member rather than a new column, and so the stored value
/// says what the bytes are.
/// </remarks>
public enum ReportFormat
{
    Csv = 1,
}

/// <summary>Whether the caller waited for the rows or was told when they were ready.</summary>
public enum ReportRunMode
{
    /// <summary>Small enough to answer in the request. The rows come back in the response.</summary>
    Synchronous = 1,

    /// <summary>Queued for the worker; the requester is emailed when the file is ready.</summary>
    Asynchronous = 2,
}

/// <summary>Where a report run has got to.</summary>
public enum ReportJobStatus
{
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
}

/// <summary>Which of the two rollups ran.</summary>
public enum RollupKind
{
    /// <summary>Every few minutes: rebuilds only the days whose source rows changed.</summary>
    Incremental = 1,

    /// <summary>Nightly: rebuilds every day in the window from source, whatever the watermark says.</summary>
    FullRebuild = 2,
}

/// <summary>How a rollup run ended.</summary>
public enum RollupRunStatus
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}
