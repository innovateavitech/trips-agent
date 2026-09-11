using System.Diagnostics;
using System.Text;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using TripsAgent.Api.Assets;
using TripsAgent.Api.Authorization;
using TripsAgent.Api.Catalog;
using TripsAgent.Api.Identity;
using TripsAgent.Api.Payments;
using TripsAgent.Api.Pricing;
using TripsAgent.Api.Scheduling;
using TripsAgent.Api.Search;
using TripsAgent.Api.Storage;
using TripsAgent.Api.Tenancy;
using TripsAgent.Application;
using TripsAgent.Application.Identity;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Scheduling;
using TripsAgent.Integrations.Paystack;
using TripsAgent.Integrations.TripsAfrica;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// The API publishes; it never consumes. Consumers run in TripsAgent.Worker so that the two scale
// on different signals — the API on request rate, the Worker on queue depth.
builder.Services.AddMessagePublishing(builder.Configuration);

// Paystack behind IPaymentGateway. Nothing above this line knows which gateway is in use.
builder.Services.AddPaystack(builder.Configuration);
builder.Services.AddTripsAfrica(builder.Configuration);

// Storage and client only. AddJobProcessing — the part that actually executes jobs — is called by
// the Worker and must never be called here: every API instance would then race to run the cron.
builder.Services.AddJobScheduling(builder.Configuration);

var hangfireOptions =
    builder.Configuration.GetSection(HangfireOptions.SectionName).Get<HangfireOptions>()
    ?? new HangfireOptions();
// Bearer authentication. The same Jwt section that Infrastructure issues tokens from is read
// here to validate them, so the two can never disagree about the key or the audience.
var jwtOptions = TripsAgent.Infrastructure.DependencyInjection.ReadJwtOptions(builder.Configuration);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwtOptions.SigningKeyBytes()),
            ValidateLifetime = true,

            // No grace period. The default is five minutes, which would keep an expired token
            // working well past the fifteen-minute window that is the whole point of a short one.
            ClockSkew = TimeSpan.Zero,

            // Our own claim names, not the SOAP-era URIs .NET maps them to by default.
            NameClaimType = TripsClaimTypes.Subject,
            RoleClaimType = TripsClaimTypes.Role,
        };

        // Leave "sub" as "sub". With the default mapping it silently becomes a long
        // schemas.xmlsoap.org URI, and the tenant middleware would find no user id.
        options.MapInboundClaims = false;
    });

// A policy per permission code, each satisfied by a claim on the token. Registered from the
// catalogue rather than listed by hand, so a new permission cannot end up with no policy.
builder.Services.AddAuthorizationBuilder().AddPermissionPolicies();

// The outbox check reports Degraded, never Unhealthy, when messages are piling up: restarting the
// API cannot fix a backlog the Worker or the broker is causing. See OutboxBacklogHealthCheck.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("postgres")
    .AddOutboxBacklogCheck()

    // Degraded (Unhealthy in Production) when the application's role bypasses row-level security,
    // which would otherwise switch the tenant backstop off without a word. See ADR-0006.
    .AddRowLevelSecurityCheck();

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

app.UseAuthentication();
app.UseAuthorization();

// Resolves the caller's agency for the rest of the request, from the claims the token carries.
// It has to run after UseAuthentication — before that there is no identity to read.
app.UseTenantContext();

// Tells the audit log who is acting. Without it every audited change is attributed to nobody,
// which is exactly the question the log exists to answer.
app.UseAuditContext();

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
app.MapRegistrationEndpoints();
app.MapAuthenticationEndpoints();
app.MapKybEndpoints();
app.MapKybReviewEndpoints();
app.MapWalletEndpoints();
app.MapPricingEndpoints();
app.MapSearchEndpoints();

app.MapAssetEndpoints();
app.MapCatalogEndpoints();

// Anonymous and signature-authenticated, standing in for an object store's presigned URLs while
// files live on local disk. Maps nothing once a cloud adapter is registered.
app.MapLocalStorageEndpoints();

// Anonymous, and authenticated by signature instead of a token. Mapped after UseAuthentication
// so the pipeline is in place, but it deliberately requires no identity — a gateway has none.
app.MapPaymentWebhookEndpoints();

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
