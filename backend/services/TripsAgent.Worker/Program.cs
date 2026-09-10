using Hangfire;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Scheduling;

// The Worker is a separate process from the API on purpose. Both talk to the same PostgreSQL and
// the same RabbitMQ, but they are scaled on different signals: the API on request rate, the Worker
// on queue depth and job backlog. Running consumers inside the API would mean that scaling out to
// handle a traffic spike also multiplied every cron job by the number of API instances.
//
// It does two separate things, and plan §3 draws the line between them:
//
//   MassTransit — sagas, domain events, anything on the booking or money path.
//   Hangfire    — recurring jobs on a clock.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

// Consumers are registered through the callback. There are none yet — the queues are declared and
// sit empty until the checkout saga (issue #35) and the supplier poller (issue #38) arrive.
builder.Services.AddMessageConsuming(builder.Configuration);

builder.Services.AddJobProcessing(builder.Configuration);

// Graceful shutdown, the host half. On SIGTERM — which is what Docker, Kubernetes and systemd all
// send first — the host gives every hosted service this long to stop before killing the process.
//
// It must be longer than the timeouts the bus and the job server were given (30s each), or the
// host would pull the rug out from under a consumer that was about to finish cleanly.
builder.Services.Configure<HostOptions>(host =>
{
    host.ShutdownTimeout = TimeSpan.FromSeconds(45);

    // A consumer or job server that throws while starting up should take the process down so the
    // orchestrator restarts it, rather than leaving a Worker running that quietly consumes
    // nothing.
    host.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});

var host = builder.Build();

// Recurring jobs are registered here, in the one process that runs them. AddOrUpdate is idempotent,
// so this is safe on every start: a redeploy updates a schedule in place rather than duplicating it.
// Resolving the manager connects to PostgreSQL; if that fails the Worker stops, and the orchestrator
// restarts it — the same fail-fast rule the consumers and the job server follow.
AuditLogMaintenanceSchedule.Register(host.Services.GetRequiredService<IRecurringJobManager>());

await host.RunAsync();
