using System.Diagnostics;
using TripsAgent.Api.Tenancy;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("postgres");

var app = builder.Build();

// `dotnet run --project services/TripsAgent.Api -- migrate` applies pending migrations and
// exits, rather than serving traffic. Kept out of startup on purpose: see DatabaseMigrator.
if (DatabaseMigrator.IsMigrationCommand(args))
{
    return await DatabaseMigrator.RunAsync(app.Services);
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

// Resolves the caller's agency for the rest of the request. Everything that touches the
// database depends on this having run, so it goes before the endpoints. It will sit after
// UseAuthentication() once JWT issuance lands in #16.
app.UseTenantContext();

app.MapHealthChecks("/health");

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
