using Hangfire;
using TripsAgent.Application;
using TripsAgent.Documents;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Assets;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Catalog;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Payments;
using TripsAgent.Infrastructure.Retention;
using TripsAgent.Infrastructure.Scheduling;
using TripsAgent.Infrastructure.Suppliers;
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
// API never loads a scanner or an image decoder. With no real virus scanner configured outside
// Development the pipeline alone is disabled — uploads stay pending and unserved — while every other
// job here keeps running. See AssetPipelineStatus.
builder.Services.AddAssetProcessing(builder.Configuration, builder.Environment);

// Invoices and vouchers (issue #46), drawn with QuestPDF from documents.render. Here and only here:
// the API numbers a reissue and queues it, but never renders. Documents__QuestPdfLicense names the
// licence the business holds; see DocumentRenderingRegistration.
builder.Services.AddDocumentRendering(builder.Configuration);

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

// Keeps the supplier call log's monthly partitions ahead of the calendar. Without it every supplier
// call fails to record once the prepared months run out.
SupplierApiCallMaintenanceSchedule.Register(recurringJobs);

// The retention schedule (issue #105, docs/DATA_RETENTION.md). Dry run until DataRetention__DryRun is
// set to false deliberately: it counts and records what it would delete, and deletes nothing. It never
// touches financial records or the audit log. Runbook: docs/runbooks/data-retention.md.
DataRetentionSchedule.Register(recurringJobs);

// Expires uploads that never arrived and re-enqueues processing that was lost. The complete step
// enqueues each asset directly, so like the webhook drain this normally finds nothing.
AssetSweepSchedule.Register(recurringJobs);

// The booking pipeline's clockwork. The status poller is the only way a booking's outcome is ever
// learned — Trips Africa has no webhooks — and the only thing that asks for a payment to be reversed
// (#37). The time limit monitor warns agents before a held fare lapses, and lapses it after (#38).
SupplierBookingStatusPollSchedule.Register(recurringJobs);
TicketTimeLimitMonitorSchedule.Register(recurringJobs);

// The checkout's one unwatched wait: paid for, but the issue message never ran (#42). Sending it again
// is safe — the issuer sends the supplier nothing for a booking that is already issuing.
TripsAgent.Infrastructure.Checkout.CheckoutSweepSchedule.Register(recurringJobs);

// Group departures (#57), plan §3 jobs 6, 9, 10 and 11. The seats and the status are moved by the
// checkout that earns them; these are the clock's share of the work — the checkout that walked
// away, the offer nobody answered, the payment nobody made, and the nightly proof that every
// status still matches its seats.
DepartureHoldExpirySchedule.Register(recurringJobs);
WaitlistOfferExpirySchedule.Register(recurringJobs);
DepartureStatusSweepSchedule.Register(recurringJobs);
InstallmentReminderSchedule.Register(recurringJobs);

await host.RunAsync();
