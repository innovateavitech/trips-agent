using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using TripsAgent.Api.Identity;
using TripsAgent.Api.Tenancy;
using TripsAgent.Application;
using TripsAgent.Application.Identity;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

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

builder.Services.AddAuthorization();

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

app.UseAuthentication();
app.UseAuthorization();

// Resolves the caller's agency for the rest of the request, from the claims the token carries.
// It has to run after UseAuthentication — before that there is no identity to read.
app.UseTenantContext();

app.MapHealthChecks("/health");

app.MapRegistrationEndpoints();
app.MapAuthenticationEndpoints();

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
