using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Pricing;

/// <summary>
/// What PostgreSQL itself refuses, whatever the application does. Raw SQL on purpose: these are
/// the writes that would get past the domain — a script, a hand-fix in psql, a future bug.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PricingSchemaTests
{
    private const string RestrictViolation = "23001";
    private const string CheckViolation = "23514";

    private readonly PostgresFixture _postgres;

    public PricingSchemaTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------ a rule's terms never change

    [Theory]
    [InlineData("percent_basis_points = 2000")]
    [InlineData("priority = 99")]
    [InlineData("scope = 'Global', product_type = NULL")]
    [InlineData("applies_to_sub_agents = false")]
    [InlineData("effective_from = effective_from - interval '1 day'")]
    [InlineData("agency_id = gen_random_uuid()")]
    public async Task A_rules_terms_cannot_be_updated(string change)
    {
        await using var world = await WorldAsync();
        var rule = await world.AddRuleAsync();

        // EF1002 guards against user input reaching raw SQL. `change` is one of the constants in
        // [InlineData] above — a SET clause cannot be a parameter, and the test needs a real one.
#pragma warning disable EF1002
        var act = () => world.Db.Database.ExecuteSqlRawAsync(
            $"UPDATE pricing.markup_rules SET {change} WHERE id = {{0}}", rule.Id);
#pragma warning restore EF1002

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RestrictViolation);
    }

    [Fact]
    public async Task A_rule_cannot_be_deleted()
    {
        await using var world = await WorldAsync();
        var rule = await world.AddRuleAsync();

        var act = () => world.Db.Database.ExecuteSqlRawAsync("DELETE FROM pricing.markup_rules WHERE id = {0}", rule.Id);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RestrictViolation);
    }

    [Fact]
    public async Task Retiring_and_replacing_a_rule_is_allowed()
    {
        await using var world = await WorldAsync();
        var rule = await world.AddRuleAsync();

        // The two writes the trigger exists to let through, done the way the application does them.
        var replacement = rule.ReplaceWith(World.Terms(2_000), DateTimeOffset.UtcNow);
        world.Db.MarkupRules.Add(replacement);
        await world.Db.SaveChangesAsync();

        var stored = await world.Db.MarkupRules.AsNoTracking().SingleAsync(candidate => candidate.Id == rule.Id);
        stored.EffectiveTo.Should().NotBeNull();
        stored.SupersededById.Should().Be(replacement.Id);
    }

    [Fact]
    public async Task An_ended_rule_cannot_be_reopened()
    {
        await using var world = await WorldAsync();
        var rule = await world.AddRuleAsync();
        rule.Retire(DateTimeOffset.UtcNow);
        await world.Db.SaveChangesAsync();

        var act = () => world.Db.Database.ExecuteSqlRawAsync(
            "UPDATE pricing.markup_rules SET effective_to = NULL WHERE id = {0}", rule.Id);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RestrictViolation);
    }

    [Fact]
    public async Task An_ended_rule_cannot_be_extended()
    {
        await using var world = await WorldAsync();
        var rule = await world.AddRuleAsync();
        rule.Retire(DateTimeOffset.UtcNow);
        await world.Db.SaveChangesAsync();

        var act = () => world.Db.Database.ExecuteSqlRawAsync(
            "UPDATE pricing.markup_rules SET effective_to = effective_to + interval '1 day' WHERE id = {0}", rule.Id);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RestrictViolation);
    }

    [Fact]
    public async Task A_replaced_rule_cannot_be_pointed_at_a_different_replacement()
    {
        await using var world = await WorldAsync();
        var rule = await world.AddRuleAsync();
        var first = rule.ReplaceWith(World.Terms(2_000), DateTimeOffset.UtcNow);
        world.Db.MarkupRules.Add(first);
        await world.Db.SaveChangesAsync();

        var other = await world.AddRuleAsync();

        var act = () => world.Db.Database.ExecuteSqlRawAsync(
            "UPDATE pricing.markup_rules SET superseded_by_id = {0} WHERE id = {1}", other.Id, rule.Id);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RestrictViolation);
    }

    // ------------------------------------------------------------------ a rule's shape

    [Theory]
    [InlineData("'Global', NULL, NULL, 'Fixed', NULL, 1000, 10, NULL", "a fixed rule with a cap")]
    [InlineData("'Global', NULL, NULL, 'Percentage', 1000, NULL, 500, 100", "a minimum above the maximum")]
    [InlineData("'Global', NULL, NULL, 'Percentage', 100001, NULL, NULL, NULL", "a percentage above 1000%")]
    [InlineData("'Global', NULL, NULL, 'Percentage', NULL, NULL, NULL, NULL", "a percentage rule with no percentage")]
    [InlineData("'Global', 'Tour', NULL, 'Percentage', 1000, NULL, NULL, NULL", "a global rule naming a product type")]
    [InlineData("'Product', 'Tour', NULL, 'Percentage', 1000, NULL, NULL, NULL", "a product rule with no product")]
    [InlineData("'Supplier', NULL, NULL, 'Percentage', 1000, NULL, NULL, NULL", "a supplier rule with no supplier")]
    [InlineData("'Everything', NULL, NULL, 'Percentage', 1000, NULL, NULL, NULL", "an unknown scope")]
    public async Task Half_a_rule_cannot_be_written(string values, string because)
    {
        await using var world = await WorldAsync();

        // EF1002: `values` is one of the [InlineData] constants above, never input. See the note on
        // A_rules_terms_cannot_be_updated.
#pragma warning disable EF1002
        var act = () => world.Db.Database.ExecuteSqlRawAsync(
            $$"""
             INSERT INTO pricing.markup_rules
                 (id, agency_id, scope, product_type, supplier_code, calculation_type,
                  percent_basis_points, value_minor, min_markup_minor, max_markup_minor,
                  currency, priority, applies_to_sub_agents, effective_from, created_at, updated_at)
             VALUES
                 (gen_random_uuid(), {0}, {{values}},
                  'NGN', 0, true, now(), now(), now())
             """,
            world.AgencyId);
#pragma warning restore EF1002

        (await act.Should().ThrowAsync<PostgresException>(because)).Which.SqlState.Should().Be(CheckViolation);
    }

    // ------------------------------------------------------------------ quotes

    [Fact]
    public async Task A_markup_with_no_rule_behind_it_cannot_be_stored()
    {
        await using var world = await WorldAsync();

        var act = () => world.InsertQuoteAsync(net: 100_000, markup: 10_000, gross: 110_000, ruleId: null);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(CheckViolation);
    }

    [Fact]
    public async Task A_quote_whose_figures_do_not_add_up_cannot_be_stored()
    {
        await using var world = await WorldAsync();
        var rule = await world.AddRuleAsync();

        var act = () => world.InsertQuoteAsync(net: 100_000, markup: 10_000, gross: 111_000, ruleId: rule.Id);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(CheckViolation);
    }

    [Fact]
    public async Task A_quote_naming_its_rule_is_stored()
    {
        await using var world = await WorldAsync();
        var rule = await world.AddRuleAsync();

        // The control for the two above: the same insert with honest figures goes through, so
        // they fail for the reason they claim to.
        await world.InsertQuoteAsync(net: 100_000, markup: 10_000, gross: 110_000, ruleId: rule.Id);

        (await world.Db.PriceQuotes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_rule_that_priced_a_quote_is_protected_by_the_foreign_key_too()
    {
        await using var world = await WorldAsync();
        var rule = await world.AddRuleAsync();
        await world.InsertQuoteAsync(net: 100_000, markup: 10_000, gross: 110_000, ruleId: rule.Id);

        // Drop the trigger to prove the second line of defence stands on its own.
        await world.Db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE pricing.markup_rules DISABLE TRIGGER markup_rules_terms_immutable_trg");

        var act = () => world.Db.Database.ExecuteSqlRawAsync("DELETE FROM pricing.markup_rules WHERE id = {0}", rule.Id);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");
    }

    // ------------------------------------------------------------------ tenancy

    [Fact]
    public async Task One_agency_cannot_see_anothers_rules_or_quotes()
    {
        await using var world = await WorldAsync();
        var rule = await world.AddRuleAsync();
        await world.InsertQuoteAsync(net: 100_000, markup: 10_000, gross: 110_000, ruleId: rule.Id);

        Guid otherId;
        await using (var setup = _postgres.Connect(world.Database))
        {
            var other = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");
            setup.Agencies.Add(other);
            await setup.SaveChangesAsync();
            otherId = other.Id;
        }

        var tenancy = TestTenancy.For(otherId);
        await using var asOther = _postgres.Connect(world.Database, tenancy.Tenant, tenancy.Scope);

        (await asOther.MarkupRules.CountAsync()).Should().Be(0);
        (await asOther.PriceQuotes.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<World> WorldAsync()
    {
        // A fresh name every time, including for each case of a [Theory]. See RegistrationEndToEndTests.
        var database = $"pricing_schema_{Guid.NewGuid():N}";

        Guid agencyId;
        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();

            var agency = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            setup.Agencies.Add(agency);
            await setup.SaveChangesAsync();
            agencyId = agency.Id;
        }

        var tenancy = TestTenancy.For(agencyId);
        return new World(database, agencyId, _postgres.Connect(database, tenancy.Tenant, tenancy.Scope));
    }

    private sealed class World : IAsyncDisposable
    {
        public World(string database, Guid agencyId, AppDbContext db)
        {
            Database = database;
            AgencyId = agencyId;
            Db = db;
        }

        public string Database { get; }

        public Guid AgencyId { get; }

        public AppDbContext Db { get; }

        public static MarkupRuleTerms Terms(int percent) => new()
        {
            Scope = MarkupScope.ProductType,
            ProductType = PricedProductType.Tour,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = percent,
            MinMarkupMinor = new Money(100),
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
        };

        public async Task<MarkupRule> AddRuleAsync()
        {
            var rule = MarkupRule.Create(AgencyId, Terms(1_000));
            Db.MarkupRules.Add(rule);
            await Db.SaveChangesAsync();
            return rule;
        }

        public Task<int> InsertQuoteAsync(long net, long markup, long gross, Guid? ruleId) =>
            Db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO pricing.price_quotes
                    (id, agency_id, product_type, currency, net_amount_minor, markup_amount_minor,
                     gross_amount_minor, markup_rule_id, created_at, updated_at)
                VALUES (gen_random_uuid(), {0}, 'Tour', 'NGN', {1}, {2}, {3}, {4}, now(), now())
                """,
                AgencyId, net, markup, gross, (object?)ruleId ?? DBNull.Value);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
