using System.Text.RegularExpressions;
using TripsAgent.Domain.Auditing;
using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Suppliers;

/// <summary>
/// A travel aggregator we buy inventory from. Trips Africa is the first; it is a row, not a
/// type, so the second is an insert rather than a schema change.
/// </summary>
/// <remarks>
/// Platform-wide reference data. Not tenant-scoped: every agency books through the same
/// suppliers, and none of them owns one.
/// </remarks>
public sealed partial class Supplier : Entity, IAuditableEntity, IAuditLogged
{
    /// <summary>The longest supplier code the column allows.</summary>
    public const int MaxCodeLength = 40;

    private Supplier()
    {
        Code = string.Empty;
        Name = string.Empty;
        BaseUrl = string.Empty;
        Config = "{}";
    }

    /// <summary>Adds a supplier to the catalogue.</summary>
    /// <param name="code">
    /// Stable machine name, lower-case snake case — <c>trips_africa</c>. Adapters are looked up by
    /// it, so it never changes once bookings exist.
    /// </param>
    /// <param name="configJson">
    /// Non-secret per-supplier settings as a JSON object. Secrets live in
    /// <see cref="SupplierCredential"/>, encrypted, never here.
    /// </param>
    public static Supplier Register(string code, string name, SupplierKind kind, string baseUrl, string? configJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        if (!IsValidCode(code))
        {
            throw new ArgumentException(
                $"Supplier code '{code}' must be lower-case letters, digits and underscores, starting with a letter.",
                nameof(code));
        }

        return new Supplier
        {
            Code = code,
            Name = name.Trim(),
            Kind = kind,
            BaseUrl = baseUrl.Trim(),
            Config = string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson,
            IsActive = true,
        };
    }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public SupplierKind Kind { get; private set; }

    public string BaseUrl { get; private set; }

    /// <summary>Non-secret settings, as a JSON object.</summary>
    public string Config { get; private set; }

    /// <summary>False stops new searches going to this supplier without touching existing bookings.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    /// <summary>True when <paramref name="code"/> is a well-formed supplier code.</summary>
    public static bool IsValidCode(string code) =>
        !string.IsNullOrEmpty(code) && code.Length <= MaxCodeLength && CodePattern().IsMatch(code);

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
