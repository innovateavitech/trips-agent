namespace TripsAgent.Infrastructure.Assets;

/// <summary>
/// Whether this Worker can run the asset pipeline, and if not, why.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline fails closed; the Worker stays up. Without a real virus scanner nothing can be
/// scanned, so nothing may be marked clean. But refusing to start the whole Worker — which is what
/// this did first — would also stop the payment-webhook drain, the nightly ledger integrity audit,
/// audit-log maintenance and every message consumer, none of which has anything to do with uploads.
/// </para>
/// <para>
/// So a missing, unknown or development-only scanner disables this one pipeline. Uploads stay
/// pending and are never served — the serving gate and the <c>ck_assets_ready_only_when_clean</c>
/// constraint guarantee that. The processing and sweep jobs skip, and
/// <see cref="AssetPipelineStatusReporter"/> says so, at critical level, at startup.
/// </para>
/// </remarks>
public sealed class AssetPipelineStatus
{
    private AssetPipelineStatus(bool isEnabled, string? disabledReason)
    {
        IsEnabled = isEnabled;
        DisabledReason = disabledReason;
    }

    /// <summary>A real scanner is registered; the pipeline runs.</summary>
    public static AssetPipelineStatus Enabled { get; } = new(true, null);

    /// <summary>No usable scanner; the pipeline does nothing, for the reason given.</summary>
    public static AssetPipelineStatus Disabled(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, reason);
    }

    public bool IsEnabled { get; }

    /// <summary>Why the pipeline is disabled, in words an operator can act on. Null when enabled.</summary>
    public string? DisabledReason { get; }
}
