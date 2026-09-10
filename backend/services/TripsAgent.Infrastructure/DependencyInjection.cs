using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Tenancy;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Identity;
using TripsAgent.Infrastructure.Notifications;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Tenancy;

namespace TripsAgent.Infrastructure;

/// <summary>
/// The one place Infrastructure is wired into the container. The API and the Worker both call
/// <see cref="AddInfrastructure"/>, so they cannot drift apart in how they talk to the database.
/// </summary>
public static class DependencyInjection
{
    /// <summary>The configuration key holding the PostgreSQL connection string.</summary>
    public const string PostgresConnectionName = "Postgres";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(PostgresConnectionName);

        // Whitespace, not just null: appsettings.json declares the key with an empty value so
        // the shape of the configuration is discoverable, and an empty string would otherwise
        // sail through to Npgsql and fail with something far less helpful than this.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"""
                 No PostgreSQL connection string configured.

                 Add one under ConnectionStrings:{PostgresConnectionName} in appsettings.Development.json,
                 or set the environment variable ConnectionStrings__{PostgresConnectionName}.

                 Local default: {AppDbContextFactory.LocalDevelopmentConnectionString}
                 Start the database with: docker compose up -d postgres
                 """);
        }

        // A clock we can replace in tests. Nothing should call DateTimeOffset.UtcNow directly.
        services.TryAddSingletonTimeProvider();

        services.AddAuditing(configuration);

        // Tenancy is scoped: one resolved agency per request, and nothing shared between them.
        // TenantContext is registered as itself as well as behind the interface, because
        // middleware needs the concrete type to call SetTenant while everything downstream
        // should only be able to read.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IPlatformScope, PlatformScope>();

        // Stateless and thread-safe, so one instance serves the whole process.
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();

        // Resolved lazily, so `migrate` and `seed` run without a hashing key configured — only a
        // request that actually hashes a token needs one, and it fails clearly if it is missing.
        services.AddSingleton<ITokenHasher>(_ => new HmacTokenHasher(ReadTokenHashKey(configuration)));

        services.AddSingleton<IEmailSender>(_ => new SmtpEmailSender(ReadSmtpOptions(configuration)));

        services.AddSingleton(ReadJwtOptions(configuration));
        services.AddSingleton<IAccessTokenIssuer>(sp => new JwtAccessTokenIssuer(
            sp.GetRequiredService<JwtOptions>(),
            sp.GetRequiredService<TimeProvider>()));

        // The service-provider overload: the audit interceptor below has to come from the scoped
        // provider so it sees the actor for *this* request.
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            // The tenant write guard is not registered here: AppDbContext installs it in
            // OnConfiguring, so it is present however the context was constructed.
            options
                .UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);

                    // Transient network blips are worth a retry; a deadlock is not.
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorCodesToAdd: null);
                })
                // users.email is citext, agencies.path is ltree, and every column is snake_case.
                .UseSnakeCaseNamingConvention()

                // Resolved from the scoped provider so the interceptor sees the actor for *this*
                // request. A singleton would freeze whoever made the first request into every
                // audit row that followed.
                .AddInterceptors(serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>());
        });

        // Use cases see the database through this port; it is the same scoped context, so the
        // same tenant filters, audit interceptor and write guard all still apply.
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        return services;
    }

    /// <summary>The configuration key holding the base64 HMAC key for generated secrets.</summary>
    public const string TokenHashKeySetting = "Security:TokenHashKey";

    /// <summary>
    /// Reads the JWT settings. Public because the API needs the same values to <i>validate</i>
    /// tokens that this assembly uses to <i>issue</i> them — two readers of one section, never
    /// two copies of the defaults.
    /// </summary>
    public static JwtOptions ReadJwtOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var jwt = configuration.GetSection("Jwt");

        return new JwtOptions
        {
            Issuer = jwt["Issuer"] ?? "https://tripsagent.local",
            Audience = jwt["Audience"] ?? "trips-agent-api",
            SigningKey = jwt["SigningKey"] ?? string.Empty,
            AccessTokenLifetime = int.TryParse(
                jwt["AccessTokenMinutes"], System.Globalization.CultureInfo.InvariantCulture, out var minutes)
                ? TimeSpan.FromMinutes(minutes)
                : TimeSpan.FromMinutes(15),
        };
    }

    private static byte[] ReadTokenHashKey(IConfiguration configuration)
    {
        var encoded = configuration[TokenHashKeySetting];

        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException(
                $"""
                 No token hashing key configured at {TokenHashKeySetting}.

                 It keys the HMAC used to store verification codes and tokens, and it must be at
                 least {HmacTokenHasher.MinimumKeyBytes} random bytes, base64-encoded. Generate one with:

                     openssl rand -base64 32

                 then set it as the environment variable Security__TokenHashKey. Never commit a
                 production key — rotating it invalidates every outstanding code, which is the
                 point if it leaks.
                 """);
        }

        try
        {
            return Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{TokenHashKeySetting} is not valid base64.", ex);
        }
    }

    private static SmtpOptions ReadSmtpOptions(IConfiguration configuration)
    {
        var smtp = configuration.GetSection("Smtp");

        return new SmtpOptions
        {
            Host = smtp["Host"] ?? "localhost",
            Port = int.TryParse(smtp["Port"], System.Globalization.CultureInfo.InvariantCulture, out var port) ? port : 1025,
            SecureSocket = Enum.TryParse<SecureSocketOptions>(smtp["SecureSocket"], ignoreCase: true, out var secure)
                ? secure
                : SecureSocketOptions.StartTls,
            Username = smtp["Username"],
            Password = smtp["Password"],
            FromAddress = smtp["FromAddress"] ?? "no-reply@tripsagent.test",
            FromName = smtp["FromName"] ?? "Trips Agent",
        };
    }

    /// <summary>
    /// The audit trail: the ambient actor, the redaction policy, the interceptor that turns a save
    /// into a record of who changed what, and the partition maintenance job.
    /// </summary>
    private static void AddAuditing(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AuditLogOptions.SectionName);

        // Bound, then validated. A zero in configuration must reach the check below and fail it —
        // not be skipped over in favour of the default, which would quietly keep data for seven
        // years that someone had configured to keep for none.
        services.AddOptions<AuditLogOptions>()
            .Configure(options => section.Bind(options))
            .Validate(
                options => options.RetentionMonths >= 1 && options.PartitionsCreatedAhead >= 1,
                "AuditLog:RetentionMonths and AuditLog:PartitionsCreatedAhead must both be at least 1. "
                + "A retention of zero would drop the month still being written to.");

        // Stateless once built, so one instance serves every request.
        services.AddSingleton<AuditRedactionPolicy>();

        // One actor per request or job run. AuditContext is registered as itself as well, so the
        // edge — authentication middleware, a job host — can populate what IAuditContext only
        // exposes for reading.
        services.AddScoped<AuditContext>();
        services.AddScoped<IAuditContext>(provider => provider.GetRequiredService<AuditContext>());

        services.AddScoped<AuditSaveChangesInterceptor>();
        services.AddScoped<IAuditLogMaintenance, AuditLogPartitionMaintenance>();
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
