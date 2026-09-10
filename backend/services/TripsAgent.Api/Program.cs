using System.Diagnostics;
using Hangfire;
using TripsAgent.Api.Scheduling;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Scheduling;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

// The API publishes; it never consumes. Consumers run in TripsAgent.Worker so that the two scale
// on different signals — the API on request rate, the Worker on queue depth.
builder.Services.AddMessagePublishing(builder.Configuration);

// Storage and client only. AddJobProcessing — the part that actually executes jobs — is called by
// the Worker and must never be called here: every API instance would then race to run the cron.
builder.Services.AddJobScheduling(builder.Configuration);

var hangfireOptions =
    builder.Configuration.GetSection(HangfireOptions.SectionName).Get<HangfireOptions>()
    ?? new HangfireOptions();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("postgres");

var app = builder.Build();

// `dotnet run --project services/TripsAgent.Api -- migrate` applies pending migrations and
// exits, rather than serving traffic. Kept out of startup on purpose: see DatabaseMigrator.
if (DatabaseMigrator.IsMigrationCommand(args))
{
    return await DatabaseMigrator.RunAsync(app.Services);
}

// `dotnet run --project services/TripsAgent.Api -- audit-maintenance` prepares the coming months'
// audit partitions and drops the expired ones, then exits. See AuditLogMaintenanceCommand.
if (AuditLogMaintenanceCommand.IsMaintenanceCommand(args))
{
    return await AuditLogMaintenanceCommand.RunAsync(app.Services);
}

// `dotnet run --project services/TripsAgent.Api -- seed` loads the development dataset.
if (DatabaseSeeder.IsSeedCommand(args))
{
    return await DatabaseSeeder.RunAsync(app.Services);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");

// The Hangfire dashboard can requeue, delete and trigger jobs, several of which move money. It is
// off unless switched on, and even then it is behind HangfireDashboardPolicy — which denies
// everyone until identity lands in issue #12, apart from local requests in Development.
if (hangfireOptions.DashboardEnabled)
{
    var dashboardPolicy = new HangfireDashboardPolicy(
        hangfireOptions.DashboardRole,
        allowUnauthenticatedLocalRequests:
            hangfireOptions.AllowLocalRequestsWithoutAuthentication && app.Environment.IsDevelopment());

    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [new HangfireDashboardAuthorizationFilter(dashboardPolicy)],

        // Read-only would be safer, but a dashboard you cannot requeue from is not much use during
        // an incident. The protection is the authorisation filter above, not this flag.
        IsReadOnlyFunc = _ => false,
        DisplayStorageConnectionString = false,
    });
}

app.MapGet("/", () => Results.Ok(new
{
    service = "TripsAgent.Api",
    status = "ok",
    environment = app.Environment.EnvironmentName,
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
}))
.WithName("ServiceInfo");

// Correlate every request so a log line can be traced back to a booking.
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                        ?? Activity.Current?.Id
                        ?? context.TraceIdentifier;

    context.Response.Headers["X-Correlation-Id"] = correlationId;
    await next();
});

await app.RunAsync();

return 0;

/// <summary>Exposed so integration tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
