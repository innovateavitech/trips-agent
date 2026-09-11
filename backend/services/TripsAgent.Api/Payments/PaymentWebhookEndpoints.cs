using TripsAgent.Application.Payments;

namespace TripsAgent.Api.Payments;

/// <summary>
/// Where payment gateways post to.
/// </summary>
/// <remarks>
/// <para>
/// Anonymous, because a gateway has no bearer token. The signature <i>is</i> the authentication,
/// which is why it is checked before the body is parsed and why the raw bytes are read rather
/// than model-bound — deserialising and re-serialising changes whitespace and key order, and the
/// HMAC would never match again.
/// </para>
/// <para>
/// 200 for a delivery we recorded, including a duplicate. A gateway reads any other status as
/// "try again", so a failure in our own <i>processing</i> is recorded and retried on our side,
/// never reported back to theirs.
/// </para>
/// <para>
/// The exception is a delivery we could not <i>record</i>. Answering 200 for that would lose it
/// for good — nothing on our side knows it arrived — so it surfaces as a 5xx and the gateway
/// redelivers. That is safe: once one delivery is recorded, the unique index on the event id
/// turns the rest into duplicates.
/// </para>
/// </remarks>
public static class PaymentWebhookEndpoints
{
    /// <summary>
    /// A ceiling on a body we will read from an unauthenticated caller.
    /// </summary>
    /// <remarks>
    /// Paystack's payloads are a few kilobytes. The limit is here because the signature cannot be
    /// checked until the whole body is in memory, so without it anyone who finds this URL can
    /// make us buffer as much as they like.
    /// </remarks>
    public const int MaxBodyBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapPaymentWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/v1/webhooks/paystack", async (
                HttpRequest request,
                PaymentWebhookHandler handler,
                CancellationToken cancellationToken) =>
            {
                if (request.ContentLength > MaxBodyBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                var body = await ReadBodyAsync(request, cancellationToken);

                if (body is null)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                var signature = request.Headers["x-paystack-signature"].FirstOrDefault();

                var outcome = await handler.ReceiveAsync(body, signature, cancellationToken);

                // 401 on a bad signature and nothing else. No detail: an unauthenticated caller
                // learns only that it failed, never which part.
                return outcome == WebhookOutcome.Rejected
                    ? Results.StatusCode(StatusCodes.Status401Unauthorized)
                    : Results.Ok(new { received = true });
            })
            .AllowAnonymous()
            .WithTags("Webhooks")
            .WithName("ReceivePaystackWebhook")

            // Kept out of the public schema: it is Paystack's contract, not ours, and publishing
            // it only advertises an unauthenticated endpoint.
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>Reads the raw body, exactly as sent. Null if it exceeds the ceiling.</summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        // ContentLength can be absent on a chunked request, so the cap is enforced while reading
        // rather than trusted from the header.
        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];

        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
