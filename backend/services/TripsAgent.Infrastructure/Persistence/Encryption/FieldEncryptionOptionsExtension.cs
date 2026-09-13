using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Security;
using TripsAgent.Infrastructure.Security;

namespace TripsAgent.Infrastructure.Persistence.Encryption;

/// <summary>
/// Carries the field encryptor on a context's options, where <c>OnModelCreating</c> can find it.
/// </summary>
/// <remarks>
/// <para>
/// A value converter is part of the model, and EF Core builds the model once and caches it. So the
/// encryptor has to be known when the model is built, and two contexts with different key rings must
/// not share one model. <see cref="FieldEncryptionModelCacheKeyFactory"/> puts the key ring's identity
/// into the cache key: every context in production shares one model, and a test with its own keys
/// gets its own.
/// </para>
/// <para>
/// A context built with no encryptor — a design-time migration, a tool — gets
/// <see cref="UnconfiguredFieldEncryptor"/> from <c>AppDbContext.OnConfiguring</c>, and fails clearly
/// the first time it touches an encrypted column.
/// </para>
/// </remarks>
public sealed class FieldEncryptionOptionsExtension : IDbContextOptionsExtension
{
    public FieldEncryptionOptionsExtension(IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(encryptor);

        Encryptor = encryptor;

        // Equal key rings share a model; anything else is told apart by reference.
        CacheIdentity = encryptor switch
        {
            AesGcmFieldEncryptor aes => aes.Fingerprint,
            UnconfiguredFieldEncryptor => "unconfigured",
            _ => encryptor,
        };
    }

    public IFieldEncryptor Encryptor { get; }

    /// <summary>What distinguishes one key ring's model from another's.</summary>
    public object CacheIdentity { get; }

    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    /// <summary>The encryptor on <paramref name="context"/>'s options.</summary>
    public static IFieldEncryptor EncryptorOf(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.GetService<IDbContextOptions>().FindExtension<FieldEncryptionOptionsExtension>()?.Encryptor
               ?? UnconfiguredFieldEncryptor.Instance;
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => string.Empty;

        // The encryptor changes no EF service, so every context shares one internal service provider.
        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);

            // The key ring's identity is a digest of the keys; the debug info gets a type name only.
            debugInfo["TripsAgent:FieldEncryption"] = ((FieldEncryptionOptionsExtension)Extension).Encryptor.GetType().Name;
        }
    }
}

/// <summary>One model per context type, design-time flag and key ring.</summary>
public sealed class FieldEncryptionModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);

        var identity = context.GetService<IDbContextOptions>().FindExtension<FieldEncryptionOptionsExtension>()?.CacheIdentity
                       ?? "unconfigured";

        return (context.GetType(), designTime, identity);
    }
}

/// <summary>Registers an encryptor on a context's options.</summary>
public static class FieldEncryptionDbContextOptionsBuilderExtensions
{
    /// <summary>Encrypts the model's encrypted columns with <paramref name="encryptor"/>.</summary>
    public static DbContextOptionsBuilder UseFieldEncryption(this DbContextOptionsBuilder builder, IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new FieldEncryptionOptionsExtension(encryptor));
        return builder;
    }

    /// <inheritdoc cref="UseFieldEncryption(DbContextOptionsBuilder, IFieldEncryptor)"/>
    public static DbContextOptionsBuilder<TContext> UseFieldEncryption<TContext>(this DbContextOptionsBuilder<TContext> builder, IFieldEncryptor encryptor)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseFieldEncryption((DbContextOptionsBuilder)builder, encryptor);
}
