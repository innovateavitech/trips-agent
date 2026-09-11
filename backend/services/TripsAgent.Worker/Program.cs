using Hangfire;
using TripsAgent.Application;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Assets;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Payments;
using TripsAgent.Infrastructure.Scheduling;
using TripsAgent.Integrations.Paystack;

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

// The use cases, because Hangfire resolves them by interface when a job runs — the webhook
// drain reaches IPaymentWebhookProcessor, which is one of these.
builder.Services.AddApplication();

builder.Services.AddInfrastructure(builder.Configuration);

// And the gateway those handlers call. The Worker verifies payments the same way the API does:
// by asking Paystack, never by trusting a payload.
builder.Services.AddPaystack(builder.Configuration);

// Consumers are registered through the callback. There are none yet — the queues are declared and
// sit empty until the checkout saga (issue #35) and the supplier poller (issue #38) arrive.
builder.Services.AddMessageConsuming(builder.Configuration);

// Publishes platform.outbox_messages to RabbitMQ every Outbox:PollInterval (issue #30), through the
// bus registered just above. Only the Worker does this: the API writes to the outbox but never
// dispatches, so scaling the API out never multiplies publishers.
builder.Services.AddOutboxDispatcher();

builder.Services.AddJobProcessing(builder.Configuration);

// The asset pipeline (issue #18): virus scan, EXIF strip, resize, WebP. Here and only here, so the
// API never loads a scanner or an image decoder. Throws — and so the Worker does not start — when
// no real virus scanner is configured outside Development. See AssetProcessingRegistration.
builder.Services.AddAssetProcessing(builder.Configuration, builder.Environment);

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
var recurringJobs = host.Services.GetRequiredService<IRecurringJobManager>();

AuditLogMaintenanceSchedule.Register(recurringJobs);

// The backstop for a webhook that was recorded but never processed. The receiver enqueues each
// event directly, so this normally finds nothing — which is the point of having it.
PaymentWebhookDrainSchedule.Register(recurringJobs);

// The nightly proof that the books balance. Everything it looks for should be impossible, which
// is precisely why it is checked — an unverified control and a broken one look identical.
LedgerIntegrityAuditSchedule.Register(recurringJobs);

// Expires uploads that never arrived and re-enqueues processing that was lost. The complete step
// enqueues each asset directly, so like the webhook drain this normally finds nothing.
AssetSweepSchedule.Register(recurringJobs);

await host.RunAsync();
