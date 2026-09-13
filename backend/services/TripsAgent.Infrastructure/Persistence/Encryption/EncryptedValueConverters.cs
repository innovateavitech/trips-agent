using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TripsAgent.Application.Security;
using TripsAgent.Infrastructure.Security;

namespace TripsAgent.Infrastructure.Persistence.Encryption;

/// <summary>A value converter that encrypts its column. The audit log recognises one by this, not by name.</summary>
public interface IEncryptedValueConverter
{
    /// <summary>The column the value is bound to, as the associated data: <c>schema.table.column</c>.</summary>
    public string Purpose { get; }

    /// <summary>The same converter, encrypting with <paramref name="encryptor"/>.</summary>
    public ValueConverter BindTo(IFieldEncryptor encryptor);
}

/// <summary>A string column stored as ciphertext.</summary>
public sealed class EncryptedStringConverter : ValueConverter<string, byte[]>, IEncryptedValueConverter
{
    public EncryptedStringConverter(IFieldEncryptor encryptor, string purpose)
        : base(
            plaintext => Encrypt(encryptor, plaintext, purpose),
            ciphertext => encryptor.Decrypt(ciphertext, purpose))
    {
        Purpose = purpose;
    }

    public string Purpose { get; }

    public ValueConverter BindTo(IFieldEncryptor encryptor) => new EncryptedStringConverter(encryptor, Purpose);

    private static byte[] Encrypt(IFieldEncryptor encryptor, string plaintext, string purpose) =>
        encryptor.Encrypt(plaintext, purpose);
}

/// <summary>A date column — a passport's expiry — stored as ciphertext of its ISO form.</summary>
public sealed class EncryptedDateConverter : ValueConverter<DateOnly, byte[]>, IEncryptedValueConverter
{
    private const string IsoDate = "yyyy-MM-dd";

    public EncryptedDateConverter(IFieldEncryptor encryptor, string purpose)
        : base(
            date => Encrypt(encryptor, date, purpose),
            ciphertext => Decrypt(encryptor, ciphertext, purpose))
    {
        Purpose = purpose;
    }

    public string Purpose { get; }

    public ValueConverter BindTo(IFieldEncryptor encryptor) => new EncryptedDateConverter(encryptor, Purpose);

    private static byte[] Encrypt(IFieldEncryptor encryptor, DateOnly date, string purpose) =>
        encryptor.Encrypt(date.ToString(IsoDate, CultureInfo.InvariantCulture), purpose);

    private static DateOnly Decrypt(IFieldEncryptor encryptor, byte[] ciphertext, string purpose) =>
        DateOnly.ParseExact(encryptor.Decrypt(ciphertext, purpose), IsoDate, CultureInfo.InvariantCulture);
}

/// <summary>
/// Marks a property encrypted at rest, in an entity configuration.
/// </summary>
/// <remarks>
/// <para>
/// A configuration class has no encryptor to hand, so this installs a converter bound to
/// <see cref="UnconfiguredFieldEncryptor"/> and <see cref="FieldEncryptionModel.Bind"/> swaps in the
/// context's real one when the model is built. The column type is <c>bytea</c> and its name ends in
/// <c>_encrypted</c>, so nobody reading the schema takes it for something they can search.
/// </para>
/// </remarks>
public static class EncryptedPropertyBuilderExtensions
{
    /// <summary>A string or date stored encrypted in <paramref name="columnName"/>, under <paramref name="purpose"/>.</summary>
    public static PropertyBuilder<T> IsEncryptedAtRest<T>(this PropertyBuilder<T> property, string columnName, string purpose)
    {
        ValueConverter converter = typeof(T) == typeof(string)
            ? new EncryptedStringConverter(UnconfiguredFieldEncryptor.Instance, purpose)
            : typeof(T) == typeof(DateOnly) || typeof(T) == typeof(DateOnly?)
                ? new EncryptedDateConverter(UnconfiguredFieldEncryptor.Instance, purpose)
                : throw new NotSupportedException($"Encrypting a {typeof(T).Name} column is not supported; only strings and dates are.");

        return Configure(property, columnName, converter);
    }

    private static PropertyBuilder<T> Configure<T>(PropertyBuilder<T> property, string columnName, ValueConverter converter)
    {
        ArgumentNullException.ThrowIfNull(property);

        if (!columnName.EndsWith("_encrypted", StringComparison.Ordinal))
        {
            throw new ArgumentException($"An encrypted column's name ends in _encrypted; '{columnName}' does not.", nameof(columnName));
        }

        return property.HasConversion(converter).HasColumnName(columnName).HasColumnType("bytea");
    }
}

/// <summary>Binds every encrypted property in a model to the context's encryptor.</summary>
public static class FieldEncryptionModel
{
    /// <summary>Called from <c>OnModelCreating</c>, after the entity configurations.</summary>
    public static void Bind(ModelBuilder modelBuilder, IFieldEncryptor encryptor)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(encryptor);

        foreach (var property in EncryptedProperties(modelBuilder.Model))
        {
            property.SetValueConverter(((IEncryptedValueConverter)property.GetValueConverter()!).BindTo(encryptor));
        }
    }

    /// <summary>Every property stored as ciphertext.</summary>
    public static IEnumerable<IMutableProperty> EncryptedProperties(IMutableModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.GetEntityTypes()
            .SelectMany(entity => entity.GetDeclaredProperties())
            .Where(property => property.GetValueConverter() is IEncryptedValueConverter)
            .ToList();
    }

    /// <summary>True when <paramref name="property"/> is stored as ciphertext.</summary>
    public static bool IsEncrypted(IReadOnlyProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.GetValueConverter() is IEncryptedValueConverter;
    }
}
