using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Auditing;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Identity;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Security;
using TripsAgent.Application.Storage;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy;
using TripsAgent.Infrastructure.Auditing;
using TripsAgent.Infrastructure.Concurrency;
using TripsAgent.Infrastructure.Documents;
using TripsAgent.Infrastructure.Identity;
using TripsAgent.Infrastructure.Messaging;
using TripsAgent.Infrastructure.Notifications;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Pricing;
using TripsAgent.Infrastructure.RateLimiting;
using TripsAgent.Infrastructure.Retention;
using TripsAgent.Infrastructure.Search;
using TripsAgent.Infrastructure.Security;
using TripsAgent.Infrastructure.Storage;
using TripsAgent.Infrastructure.Suppliers;
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

    /// <summary>
    /// The configuration key for the schema owner's connection string — migrations and DDL only.
    /// Falls back to <see cref="PostgresConnectionName"/> until the roles are split. See ADR-0006.
    /// </summary>
    public const string PostgresAdminConnectionName = "PostgresAdmin";

    /// <summary>The admin connection string, or the application's when no admin one is configured.</summary>
    public static string ReadAdminConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var admin = configuration.GetConnectionString(PostgresAdminConnectionName);

        return string.IsNullOrWhiteSpace(admin)
            ? configuration.GetConnectionString(PostgresConnectionName) ?? string.Empty
            : admin;
    }

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

        services.AddSupplierPersistence(configuration);

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

        // Sends queued notifications. Scoped, on the request's DbContext; only the Worker's
        // NotificationQueuedConsumer calls it, but registering it everywhere keeps the two hosts'
        // containers the same shape.
        services.AddScoped<NotificationDispatcher>();

        // Alerting: logs, the back-office queue and email. Scoped because it writes an
        // admin_alerts row through the request's DbContext.
        services.AddSingleton(ReadAlertOptions(configuration));
        services.AddScoped<IPlatformAlerter, Notifications.PlatformAlerter>();

        // The raw aggregate queries behind the nightly integrity audit. They live here rather
        // than on IAppDbContext, which deliberately exposes no way to run arbitrary SQL.
        services.AddScoped<ILedgerIntegrityQueries, Payments.LedgerIntegrityQueries>();

        // Lets Application tell "a unique index picked another writer" apart from every other
        // failed save, without Application referencing Npgsql.
        services.AddSingleton<IUniqueViolationDetector, PostgresUniqueViolationDetector>();

        // Gapless document numbering. Both work through the request's AppDbContext, so the counter
        // increment and the document insert share one transaction.
        services.AddScoped<ITransactionRunner, EfTransactionRunner>();
        services.AddScoped<IDocumentNumberAllocator, DocumentNumberAllocator>();

        // Files on disk, for local development. A cloud adapter arrives behind this same port once
        // a provider is chosen; nothing above it knows the difference. Registered as itself as well,
        // because the API's local storage endpoints stand in for the provider and need the concrete
        // type — and are mapped only when it is the one in use.
        services.AddSingleton(sp => new LocalBlobUrlSigner(
            () => sp.GetRequiredService<ITokenHasher>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new LocalFileBlobStorage(
            new LocalBlobStorageOptions
            {
                RootPath = configuration["Storage:LocalRoot"]
                    ?? Path.Combine(Path.GetTempPath(), "tripsagent-storage"),
            },
            sp.GetRequiredService<LocalBlobUrlSigner>()));
        services.AddSingleton<IBlobStorage>(sp => sp.GetRequiredService<LocalFileBlobStorage>());

        // The console owns the page that receives the reset token, so its address is
        // configuration rather than something this assembly can know.
        services.AddSingleton(new TripsAgent.Application.Identity.Registration.PasswordResetLinkBuilder(
            configuration["Console:PasswordResetUrl"] ?? "https://localhost:5173/reset-password"));

        // Where the gateway returns the payer to. The console owns that page, so its address is
        // configuration here for the same reason the password-reset link above is.
        services.AddSingleton(new TripsAgent.Application.Payments.TopUpCallbackUrl(
            configuration["Console:TopUpCallbackUrl"] ?? "https://localhost:5173/wallet/top-up/complete"));

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

        // The outbox and the inbox: a handler can stage messages in its own transaction, and a
        // consumer can deduplicate. Nothing here publishes — that is AddOutboxDispatcher, Worker only.
        services.AddOutbox(configuration);
        // Use cases see the database through this port; it is the same scoped context, so the
        // same tenant filters, audit interceptor and write guard all still apply.
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        // The schema owner's connection, for migrations and DDL only (ADR-0006). Keyed, so nothing
        // resolves it by accident: the application's AppDbContext is the policed one.
        services.AddSingleton(sp => new AdminDbContextFactory(
            ReadAdminConnectionString(configuration),
            sp.GetRequiredService<TimeProvider>()));

        services.AddKeyedScoped<AppDbContext>(AdminDbContextFactory.ServiceKey, (sp, _) =>
            sp.GetRequiredService<AdminDbContextFactory>().Create(
                sp.GetRequiredService<ITenantContext>(),
                sp.GetRequiredService<IPlatformScope>()));

        // Each agency's markup rules, cached in Redis — or read straight from the database when no
        // Redis is configured. Either way pricing gives the same answer; only the speed differs.
        services.AddPricingCache(configuration);

        // Net search results, per agency, for a few minutes (#40). After the pricing cache, whose
        // Redis connection it reuses.
        services.AddSearchCache(configuration);

        // The lock ticket issuance takes on an order line (#36) — the outermost of its four guards
        // against a second ticket. Redis, on the pricing cache's connection; no lock at all without it,
        // which leaves the three database guards to hold on their own.
        services.AddDistributedLocks(configuration);

        // Request counts for the API's rate limiter (issue #102), on the same Redis connection the
        // pricing cache registered — one multiplexer per process. Nothing at all without Redis, on
        // purpose: see RateLimitingRegistration.
        services.AddRateLimitStore(configuration);

        // The retention schedule's purge job (issue #105). Run by the Worker, dry run by default.
        services.AddDataRetention(configuration);

        return services;
    }

    /// <summary>
    /// Reads the alerting settings.
    /// </summary>
    /// <remarks>
    /// No recipient is a legitimate configuration — locally there is nowhere to send a P1 — so
    /// this never throws. PlatformAlerter logs a warning if a P1 is raised with nobody to email.
    /// </remarks>
    public static Notifications.PlatformAlertOptions ReadAlertOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new Notifications.PlatformAlertOptions
        {
            P1Recipient = configuration[
                $"{Notifications.PlatformAlertOptions.SectionName}:P1Recipient"],
        };
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
            WhiteLabelFromAddress = string.IsNullOrWhiteSpace(smtp["WhiteLabelFromAddress"])
                ? null
                : smtp["WhiteLabelFromAddress"],
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
        // Partition maintenance creates and drops tables, which only the schema owner may do, so it
        // runs on the admin connection rather than the policed application role (ADR-0006).
        services.AddScoped<IAuditLogMaintenance>(sp => new AuditLogPartitionMaintenance(
            sp.GetRequiredKeyedService<AppDbContext>(AdminDbContextFactory.ServiceKey),
            sp.GetRequiredService<IOptions<AuditLogOptions>>()));
    }

    /// <summary>The configuration key holding the base64 AES-256 key that encrypts stored secrets.</summary>
    public const string SecretEncryptionKeySetting = "Security:SecretEncryptionKey";

    /// <summary>
    /// Supplier credentials, their encryption, and the call log's partition maintenance. The
    /// adapters themselves are registered by their own integration projects.
    /// </summary>
    private static void AddSupplierPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        // Resolved lazily, like the token hashing key, so `migrate` and `seed` run without one — only
        // code that actually reads or writes a credential needs it, and it fails clearly if missing.
        services.AddSingleton<ISecretProtector>(_ => new AesGcmSecretProtector(ReadSecretEncryptionKey(configuration)));

        // Open question 1 as a setting: which merchant account an agency's calls use.
        services.AddSingleton(
            configuration.GetSection(SupplierCredentialOptions.SectionName).Get<SupplierCredentialOptions>()
            ?? new SupplierCredentialOptions());
        services.AddScoped<ISupplierCredentialStore, SupplierCredentialStore>();

        // The row locks the booking pipeline takes — FOR UPDATE, and SKIP LOCKED for the jobs every
        // Worker shares (#36, #37, #38). On the request's AppDbContext, so in its transaction.
        services.AddScoped<ISupplierBookingLocks, SupplierBookingLocks>();

        var section = configuration.GetSection(SupplierApiCallOptions.SectionName);

        services.AddOptions<SupplierApiCallOptions>()
            .Configure(options => section.Bind(options))
            .Validate(
                options => options.RetentionMonths >= 1 && options.PartitionsCreatedAhead >= 1,
                "SupplierApiCalls:RetentionMonths and SupplierApiCalls:PartitionsCreatedAhead must both be at least 1. "
                + "A retention of zero would drop the month still being written to.")
            .Validate(
                options => options.QueueCapacity >= 1 && options.BatchSize >= 1 && options.ShutdownDrainTimeout >= TimeSpan.Zero,
                "SupplierApiCalls:QueueCapacity and SupplierApiCalls:BatchSize must both be at least 1, and "
                + "SupplierApiCalls:ShutdownDrainTimeout must not be negative.");

        // The call log's write path (issue #39). The audit handler on every supplier client hands its
        // capture to the buffer and returns; the writer service inserts off the request path, in both
        // the Api and the Worker. Registered here, with the table, so a host that can call a supplier
        // can always record the call.
        services.AddSingleton<SupplierApiCallBuffer>();
        services.AddSingleton<ISupplierCallRecorder>(sp => sp.GetRequiredService<SupplierApiCallBuffer>());
        services.AddHostedService<SupplierApiCallWriterService>();

        // Creates and drops partitions — DDL — so it runs on the schema owner's connection (ADR-0006).
        services.AddScoped<ISupplierApiCallMaintenance>(sp => new SupplierApiCallPartitionMaintenance(
            sp.GetRequiredKeyedService<AppDbContext>(AdminDbContextFactory.ServiceKey),
            sp.GetRequiredService<IOptions<SupplierApiCallOptions>>()));
    }

    private static byte[] ReadSecretEncryptionKey(IConfiguration configuration)
    {
        var encoded = configuration[SecretEncryptionKeySetting];

        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidOperationException(
                $"""
                 No secret encryption key configured at {SecretEncryptionKeySetting}.

                 It encrypts supplier merchant keys at rest, and it must be exactly
                 {AesGcmSecretProtector.KeyBytes} random bytes, base64-encoded. Generate one with:

                     openssl rand -base64 32

                 then set it as the environment variable Security__SecretEncryptionKey. Never commit
                 a production key. Losing it makes every stored credential unreadable, so keep it in
                 the secret store alongside the database backups' keys.
                 """);
        }

        try
        {
            return Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{SecretEncryptionKeySetting} is not valid base64.", ex);
        }
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
