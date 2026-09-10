using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using TripsAgent.Application.Tenancy;
using TripsAgent.Domain.Common;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Persistence.Conventions;
using TripsAgent.Infrastructure.Tenancy;

namespace TripsAgent.UnitTests.Persistence;

/// <summary>
/// Guards the schema rules that are easy to break and expensive to discover late.
/// </summary>
/// <remarks>
/// <para>
/// Each rule is tested twice: once against a deliberately-wrong probe model, to prove the check
/// actually fires, and once against the real <see cref="AppDbContext"/>. Without the first half
/// a rule that silently matched nothing would look exactly like a rule that passed.
/// </para>
/// <para>
/// The real-model half is vacuous today — <see cref="AppDbContext"/> has no entities until the
/// agencies (#10) and identity (#13) schemas land. That is the point of putting the guards in
/// first: the rule is in place before the first table it applies to, so nobody has to remember
/// it later.
/// </para>
/// </remarks>
public class ModelConventionTests
{
    // ---------------------------------------------------------------- money columns

    [Fact]
    public void Money_maps_to_a_bigint_column()
    {
        var model = BuildProbeModel(b => b.Entity<WellNamed>().ToTable("well_named"));

        var property = model
            .FindEntityType(typeof(WellNamed))!
            .GetProperty(nameof(WellNamed.NetRateMinor));

        property.GetColumnType().Should().Be("bigint");
    }

    [Fact]
    public void Money_survives_a_round_trip_through_the_converter()
    {
        var converter = new MoneyConverter();

        // ConvertToProvider/ConvertFromProvider are already-compiled delegates over object.
        converter.ConvertToProvider(Money.FromMajor(1500)).Should().Be(150_000L);
        converter.ConvertFromProvider(150_000L).Should().Be(Money.FromMajor(1500));
    }

    [Fact]
    public void A_money_column_without_the_minor_suffix_is_reported()
    {
        var model = BuildProbeModel(b => b.Entity<BadlyNamed>().ToTable("badly_named"));

        var violations = ModelRules.MoneyColumns(model);

        violations.Should().ContainSingle()
            .Which.Column.Should().Be("net_rate");
    }

    [Fact]
    public void A_correctly_named_money_column_is_not_reported()
    {
        var model = BuildProbeModel(b => b.Entity<WellNamed>().ToTable("well_named"));

        ModelRules.MoneyColumns(model).Should().BeEmpty();
    }

    [Fact]
    public void Every_money_column_in_the_real_model_ends_in_minor()
    {
        var violations = ModelRules.MoneyColumns(RealModel());

        violations.Should().BeEmpty(BuildFailureMessage(
            "Money must live in a bigint column named with a _minor suffix", violations));
    }

    // ------------------------------------------------------- decimals and floats

    [Fact]
    public void A_decimal_column_is_reported()
    {
        var model = BuildProbeModel(b => b.Entity<HasADecimal>().ToTable("has_a_decimal"));

        var violations = ModelRules.ForbiddenNumericColumns(model);

        violations.Should().ContainSingle()
            .Which.Column.Should().Be("price");
    }

    [Fact]
    public void No_column_in_the_real_model_is_decimal_or_floating_point()
    {
        var violations = ModelRules.ForbiddenNumericColumns(RealModel());

        violations.Should().BeEmpty(BuildFailureMessage(
            "floating-point money loses kobo, and a double-entry ledger cannot recover it", violations));
    }

    // ------------------------------------------------------------------ timestamps

    [Fact]
    public void A_naive_DateTime_column_is_reported()
    {
        var model = BuildProbeModel(b => b.Entity<HasANaiveTimestamp>().ToTable("has_a_naive_timestamp"));

        var violations = ModelRules.NaiveTimestamps(model);

        violations.Should().ContainSingle()
            .Which.Column.Should().Be("issued_at");
    }

    [Fact]
    public void A_DateTimeOffset_column_is_not_reported()
    {
        var model = BuildProbeModel(b => b.Entity<WellNamed>().ToTable("well_named"));

        ModelRules.NaiveTimestamps(model).Should().BeEmpty();
    }

    [Fact]
    public void Every_timestamp_in_the_real_model_carries_an_offset()
    {
        var violations = ModelRules.NaiveTimestamps(RealModel());

        violations.Should().BeEmpty(BuildFailureMessage(
            "a DateTime has no offset, so an expiry deadline becomes ambiguous", violations));
    }

    // ------------------------------------------------------------------- extensions

    [Fact]
    public void The_model_enables_the_postgres_extensions_the_schema_needs()
    {
        // citext backs the case-insensitive unique email; ltree backs the agency hierarchy path.
        // Npgsql exposes these through its own accessor rather than as plain model annotations.
        var extensions = RealModel()
            .GetPostgresExtensions()
            .Select(extension => extension.Name)
            .ToArray();

        extensions.Should().Contain("citext");
        extensions.Should().Contain("ltree");
    }

    // ------------------------------------------------------------------------ helpers

    /// <summary>The finalised model of the real context, built without touching a database.</summary>
    private static IModel RealModel()
    {
        var tenant = new TenantContext();

        using var context = new AppDbContext(
            NpgsqlOptions<AppDbContext>(),
            TimeProvider.System,
            tenant,
            new PlatformScope(tenant, NullLogger<PlatformScope>.Instance));

        return DesignTimeModelOf(context);
    }

    /// <summary>
    /// The design-time model — the one migrations are generated from.
    /// </summary>
    /// <remarks>
    /// <c>context.Model</c> returns the <i>runtime</i> model, which EF strips of metadata the
    /// application does not need in order to execute queries. PostgreSQL extension declarations
    /// are among the casualties, so asserting against the runtime model would report no
    /// extensions even though the migration correctly creates them. Checking the design-time
    /// model is also the more honest thing to do: it is what actually becomes the schema.
    /// </remarks>
    private static IModel DesignTimeModelOf(DbContext context) =>
        context.GetService<IDesignTimeModel>().Model;

    /// <summary>
    /// A throwaway model that runs the same conventions as <see cref="AppDbContext"/>, so a probe
    /// entity is configured exactly the way a real one would be.
    /// </summary>
    private static IModel BuildProbeModel(Action<ModelBuilder> configure)
    {
        var options = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseNpgsql("Host=model-building-only;Database=none")
            .UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCacheKeyFactory, FreshModelPerContextFactory>()
            .Options;

        using var context = new ProbeDbContext(options, configure);
        return DesignTimeModelOf(context);
    }

    /// <summary>Forces EF to build a new model for every probe context.</summary>
    /// <remarks>
    /// EF caches a built model against <c>(context type, design-time)</c> — the options and the
    /// <c>OnModelCreating</c> body are deliberately <b>not</b> part of that key, because in
    /// production one context type has exactly one model. Every <see cref="ProbeDbContext"/>
    /// would therefore share whichever model the first probe test happened to build, and the
    /// remaining tests would assert against the wrong entity and quietly pass.
    /// </remarks>
    private sealed class FreshModelPerContextFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context?.GetType(), Guid.NewGuid(), designTime);
    }

    /// <summary>
    /// Options pointing at a connection string that is never opened. Building a model needs a
    /// provider — to know that Money is a bigint and DateTimeOffset a timestamptz — but not a
    /// live server, so these tests need no Docker and run in milliseconds.
    /// </summary>
    private static DbContextOptions<AppDbContext> NpgsqlOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=model-building-only;Database=none")
            .UseSnakeCaseNamingConvention()
            .Options;

    private static string BuildFailureMessage(string why, IReadOnlyCollection<ModelRules.Violation> violations) =>
        $"""
         {why}.

         Offending columns:
           {string.Join("\n  ", violations)}
         """;

    private sealed class ProbeDbContext : DbContext
    {
        private readonly Action<ModelBuilder> _configure;

        public ProbeDbContext(DbContextOptions<ProbeDbContext> options, Action<ModelBuilder> configure)
            : base(options)
        {
            _configure = configure;
        }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
            MoneyConventions.Apply(configurationBuilder);

        protected override void OnModelCreating(ModelBuilder modelBuilder) => _configure(modelBuilder);
    }

    private sealed class WellNamed
    {
        public Guid Id { get; set; }

        public Money NetRateMinor { get; set; }

        public DateTimeOffset IssuedAt { get; set; }
    }

    private sealed class BadlyNamed
    {
        public Guid Id { get; set; }

        // Missing the Minor suffix: the column becomes net_rate, which reads as though it might
        // be naira rather than kobo.
        public Money NetRate { get; set; }
    }

    // Deliberately wrong, so it is listed in backend/MoneyTypeAllowlist.txt — without that entry
    // the TRIPS001 analyser would (rightly) refuse to compile it.
    private sealed class HasADecimal
    {
        public Guid Id { get; set; }

        public decimal Price { get; set; }
    }

    private sealed class HasANaiveTimestamp
    {
        public Guid Id { get; set; }

        public DateTime IssuedAt { get; set; }
    }
}
