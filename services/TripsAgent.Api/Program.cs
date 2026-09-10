using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddPersistence(builder.Configuration.GetConnectionString(DependencyInjection.ConnectionStringName));

var app = builder.Build();

// `dotnet run --project services/TripsAgent.Api -- migrate` applies pending migrations and exits.
// Deliberately a separate command rather than something that runs on every start: two API
// instances booting at once would otherwise race each other through the same schema change.
if (args.Contains("migrate", StringComparer.OrdinalIgnoreCase))
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    var database = migrationScope.ServiceProvider.GetRequiredService<AppDbContext>().Database;
    var migrationLogger = migrationScope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    var pending = (await database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count == 0)
    {
        migrationLogger.LogInformation("Database is already up to date. Nothing to apply.");
        return;
    }

    // Joined up front rather than inside the logging call: an analyser flags work done in a log
    // argument, since it happens whether or not the level is enabled. Here it always is — this
    // command exists to report what it did — so a local keeps both the analyser and the log happy.
    var pendingNames = string.Join(", ", pending);
    migrationLogger.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, pendingNames);
    await database.MigrateAsync();
    migrationLogger.LogInformation("Database is up to date.");
    return;
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

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

app.Run();

/// <summary>Exposed so integration tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
