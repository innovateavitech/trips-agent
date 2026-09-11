using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TripsAgent.IntegrationTests.Suppliers;

/// <summary>A request the stub received: where it went and what it carried.</summary>
internal sealed record StubRequest(string Path, string Body);

/// <summary>What the stub answers.</summary>
internal sealed record StubAnswer(int Status, string Body)
{
    public static StubAnswer Json(string body, int status = 200) => new(status, body);
}

/// <summary>
/// Trips Africa, stood in by a real HTTP server on a random local port, with a journal of every
/// request that reached it.
/// </summary>
/// <remarks>
/// <para>
/// This plays the part of WireMock's request journal in #36's chaos test. WireMock is not in the test
/// stack, and this needs nothing beyond ASP.NET Core, which the tests already load: every supplier call
/// goes through the real <c>AddTripsAfrica</c> registration, the real audit handler and a real socket,
/// and the journal records each one the moment it arrives — before the stub decides what to answer. So
/// "exactly one issue call reached the supplier" is counted where the supplier would count it.
/// </para>
/// <para>
/// Answers are programmable per endpoint, so a test can make the supplier slow, silent, refusing or
/// down, exactly when it wants to.
/// </para>
/// </remarks>
internal sealed class TripsAfricaStub : IAsyncDisposable
{
    public const string IssuePath = "/api/v2/ticketing/issue";
    public const string StatusPath = "/api/Flight/GetBookingStatus";
    public const string BusReservationPath = "/api/Bus/MyReservation";

    private readonly ConcurrentQueue<StubRequest> _journal = new();
    private WebApplication? _app;

    public Uri BaseAddress { get; private set; } = new("http://127.0.0.1/");

    /// <summary>Every request, in the order it arrived.</summary>
    public IReadOnlyList<StubRequest> Journal => [.. _journal];

    public Func<StubRequest, CancellationToken, Task<StubAnswer>> OnIssue { get; set; } =
        (_, _) => Task.FromResult(Issued("TicketPending"));

    public Func<StubRequest, CancellationToken, Task<StubAnswer>> OnStatus { get; set; } =
        (_, _) => Task.FromResult(Status(3));

    /// <summary>The documented success answer to an issue call.</summary>
    public static StubAnswer Issued(string bookingStatus, string pnr = "RE6MIK") =>
        StubAnswer.Json($$"""{ "Pnr": "{{pnr}}", "IsSuccessful": true, "Message": "Successful", "BookingStatus": "{{bookingStatus}}" }""");

    /// <summary>The documented answer to a status query.</summary>
    public static StubAnswer Status(int code, string description = "status") =>
        StubAnswer.Json($$"""{ "StatusCode": {{code}}, "StatusDescription": "{{description}}", "ErrorList": [] }""");

    public static async Task<TripsAfricaStub> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var stub = new TripsAfricaStub();
        var app = builder.Build();

        app.MapPost(IssuePath, (HttpContext context) => stub.AnswerAsync(context, stub.OnIssue));
        app.MapPost(StatusPath, (HttpContext context) => stub.AnswerAsync(context, stub.OnStatus));
        app.MapPost(BusReservationPath, (HttpContext context) => stub.AnswerAsync(context, stub.OnStatus));

        await app.StartAsync();

        stub._app = app;
        stub.BaseAddress = new Uri(app.Urls.First());
        return stub;
    }

    /// <summary>How many requests reached <paramref name="path"/>.</summary>
    public int Count(string path) => _journal.Count(request => request.Path == path);

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private async Task AnswerAsync(HttpContext context, Func<StubRequest, CancellationToken, Task<StubAnswer>> behaviour)
    {
        using var reader = new StreamReader(context.Request.Body);
        var request = new StubRequest(context.Request.Path.Value ?? string.Empty, await reader.ReadToEndAsync(context.RequestAborted));

        // Journalled on arrival: the supplier has the request now, whatever happens next.
        _journal.Enqueue(request);

        var answer = await behaviour(request, context.RequestAborted);

        context.Response.StatusCode = answer.Status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(answer.Body, context.RequestAborted);
    }
}
