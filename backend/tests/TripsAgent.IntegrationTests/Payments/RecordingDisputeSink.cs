using System.Collections.Concurrent;
using TripsAgent.Application.Payments;

namespace TripsAgent.IntegrationTests.Payments;

/// <summary>Records which disputes the webhook path handed over, and how many times.</summary>
public sealed class RecordingDisputeSink : IDisputeWebhookSink
{
    public ConcurrentQueue<string> Synced { get; } = new();

    public Task<bool> SyncAsync(string gatewayDisputeId, CancellationToken cancellationToken = default)
    {
        Synced.Enqueue(gatewayDisputeId);
        return Task.FromResult(false);
    }
}
