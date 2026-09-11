using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using TripsAgent.Application.Pricing;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.Infrastructure.Pricing;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Pricing;

/// <summary>
/// Pricing against a real PostgreSQL and a real Redis: what is stored, what is inherited, and
/// when the cache is — and is not — allowed to answer.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PricingServiceTests : IClassFixture<RedisFixture>
{
    private static readonly PricingSubject Flight = new(PricedProductType.Flight, "NGN", supplierCode: "trips_africa");

    private readonly PostgresFixture _postgres;
    private readonly RedisFixture _redis;

    public PricingServiceTests(PostgresFixture postgres, RedisFixture redis)
    {
        _postgres = postgres;
        _redis = redis;
    }

    // ------------------------------------------------------------------ the winning rule id is stored

    [Fact]
    public async Task A_quote_stores_the_id_of_the_rule_that_won()
    {
        var world = await WorldAsync();
        await world.SeedRuleAsync(world.PrincipalId, Global(percent: 1_000));
        var flights = await world.SeedRuleAsync(world.PrincipalId, ForFlights(percent: 500));

        Guid quoteId;
        await using (var principal = world.SessionFor(world.PrincipalId))
        {
            quoteId = (await principal.Pricing.QuoteAsync(Flight, new Money(200_000))).Id;
        }

        // Read back through a fresh context, so this is what PostgreSQL holds and not what the
        // change tracker remembers.
        await using var check = world.SessionFor(world.PrincipalId);
        var stored = await check.Db.PriceQuotes.AsNoTracking().SingleAsync(quote => quote.Id == quoteId);

        stored.MarkupRuleId.Should().Be(flights.Id, "the product-type rule beats the global one");
        stored.NetAmountMinor.Should().Be(new Money(200_000));
        stored.MarkupAmountMinor.Should().Be(new Money(10_000));
        stored.TaxAmountMinor.Should().Be(new Money(750), "7.5% VAT on the ₦100 markup");
        stored.PlatformFeeMinor.Should().Be(Money.Zero, "no fee until subscription tiers exist");
        stored.GrossAmountMinor.Should().Be(new Money(210_750));
        stored.FxRate.Should().Be(1m);
        stored.AgencyId.Should().Be(world.PrincipalId);

        // The breakdown survived the round trip through jsonb and still explains the price alone.
        var breakdown = stored.ReadBreakdown();
        breakdown.MarkupRule!.Id.Should().Be(flights.Id);
        breakdown.MarkupRule.Summary.Should().Be("5% of the net rate");
        breakdown.VatBaseMinor.Should().Be(10_000);
        breakdown.GrossAmountMinor.Should().Be(210_750);
    }

    [Fact]
    public async Task A_quote_expires_after_the_configured_validity_by_the_clock()
    {
        var world = await WorldAsync();
        await world.SeedRuleAsync(world.PrincipalId, Global(percent: 1_000));
        var pricedAt = world.Clock.GetUtcNow();

        await using var principal = world.SessionFor(world.PrincipalId, validity: TimeSpan.FromMinutes(20));
        var quote = await principal.Pricing.QuoteAsync(Flight, new Money(100_000));

        await using var check = world.SessionFor(world.PrincipalId);
        var stored = await check.Db.PriceQuotes.AsNoTracking().SingleAsync(candidate => candidate.Id == quote.Id);

        stored.CreatedAt.Should().Be(pricedAt);
        stored.ExpiresAt.Should().Be(pricedAt.AddMinutes(20));

        world.Clock.Advance(TimeSpan.FromMinutes(19));
        stored.Invoking(q => q.EnsureUsableAt(world.Clock.GetUtcNow())).Should().NotThrow();

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        stored.Invoking(q => q.EnsureUsableAt(world.Clock.GetUtcNow())).Should().Throw<PriceQuoteExpiredException>();
    }

    [Fact]
    public async Task A_sub_agent_charges_its_own_vat_rate_on_its_principals_markup()
    {
        var world = await WorldAsync();
        await world.SeedRuleAsync(world.PrincipalId, Global(percent: 1_000));
        await world.SetVatRateAsync(world.SubAgentId, 500);

        await using var subAgent = world.SessionFor(world.SubAgentId);
        var quote = await subAgent.Pricing.QuoteAsync(Flight, new Money(100_000));

        quote.MarkupAmountMinor.Should().Be(new Money(10_000), "the principal's rule is inherited");
        quote.TaxAmountMinor.Should().Be(new Money(500), "but the VAT is the seller's own, at 5%");
    }

    [Fact]
    public async Task A_quote_with_no_applicable_rule_stores_no_markup_and_no_rule()
    {
        var world = await WorldAsync();

        await using var principal = world.SessionFor(world.PrincipalId);
        var quote = await principal.Pricing.QuoteAsync(Flight, new Money(200_000));

        quote.MarkupRuleId.Should().BeNull();
        quote.TaxAmountMinor.Should().Be(Money.Zero, "no markup, so nothing new to tax");
        quote.GrossAmountMinor.Should().Be(new Money(200_000));
    }

    [Fact]
    public async Task A_quote_keeps_naming_the_rule_that_priced_it_after_that_rule_is_edited()
    {
        var world = await WorldAsync();
        var original = await world.SeedRuleAsync(world.PrincipalId, Global(percent: 1_000));

        await using var principal = world.SessionFor(world.PrincipalId);
        var before = await principal.Pricing.QuoteAsync(Flight, new Money(100_000));

        var edited = await principal.Rules.ReplaceAsync(original.Id, Global(percent: 2_000));
        var replacement = edited.Should().BeOfType<MarkupRuleChangeOutcome.Saved>().Subject.Rule;

        var after = await principal.Pricing.QuoteAsync(Flight, new Money(100_000));

        after.MarkupRuleId.Should().Be(replacement.Id);
        after.MarkupAmountMinor.Should().Be(new Money(20_000));

        // And the first quote, and the rule it names, still say 10%.
        await using var check = world.SessionFor(world.PrincipalId);
        var old = await check.Db.PriceQuotes.AsNoTracking().SingleAsync(quote => quote.Id == before.Id);
        var oldRule = await check.Db.MarkupRules.AsNoTracking().SingleAsync(rule => rule.Id == old.MarkupRuleId);

        old.MarkupAmountMinor.Should().Be(new Money(10_000));
        old.TaxAmountMinor.Should().Be(new Money(750));
        old.GrossAmountMinor.Should().Be(new Money(110_750));
        old.ReadBreakdown().MarkupRule!.PercentBasisPoints.Should().Be(1_000, "the breakdown keeps the terms that priced it");
        oldRule.PercentBasisPoints.Should().Be(1_000);
        oldRule.SupersededById.Should().Be(replacement.Id);
    }

    // ------------------------------------------------------------------ sub-agents

    [Fact]
    public async Task A_sub_agent_with_no_rules_of_its_own_prices_with_its_principals()
    {
        var world = await WorldAsync();
        var principals = await world.SeedRuleAsync(world.PrincipalId, Global(percent: 1_000));

        await using var subAgent = world.SessionFor(world.SubAgentId);
        var quote = await subAgent.Pricing.QuoteAsync(Flight, new Money(100_000));

        quote.AgencyId.Should().Be(world.SubAgentId);
        quote.MarkupRuleId.Should().Be(principals.Id);
        quote.MarkupAmountMinor.Should().Be(new Money(10_000));
    }

    [Fact]
    public async Task A_sub_agents_own_rule_overrides_its_principals()
    {
        var world = await WorldAsync();
        await world.SeedRuleAsync(world.PrincipalId, ForFlights(percent: 1_000));
        var own = await world.SeedRuleAsync(world.SubAgentId, Global(percent: 300));

        await using var subAgent = world.SessionFor(world.SubAgentId);
        var quote = await subAgent.Pricing.QuoteAsync(Flight, new Money(100_000));

        quote.MarkupRuleId.Should().Be(own.Id);
        quote.MarkupAmountMinor.Should().Be(new Money(3_000));
    }

    [Fact]
    public async Task A_principal_rule_kept_from_sub_agents_is_not_inherited()
    {
        var world = await WorldAsync();
        await world.SeedRuleAsync(world.PrincipalId, Global(percent: 1_000) with { AppliesToSubAgents = false });

        await using var subAgent = world.SessionFor(world.SubAgentId);
        var quote = await subAgent.Pricing.QuoteAsync(Flight, new Money(100_000));

        quote.MarkupRuleId.Should().BeNull();
    }

    [Fact]
    public async Task A_sub_agent_is_shown_only_the_principal_rules_that_can_price_its_sales()
    {
        var world = await WorldAsync();
        var flights = await world.SeedRuleAsync(world.PrincipalId, ForFlights(percent: 1_500));
        var global = await world.SeedRuleAsync(world.PrincipalId, Global(percent: 1_000));
        await world.SeedRuleAsync(world.PrincipalId, Global(percent: 2_000) with { AppliesToSubAgents = false, Priority = 5 });
        await world.SeedRuleAsync(world.PrincipalId, Global(percent: 3_000) with { EffectiveTo = world.Clock.GetUtcNow().AddDays(-1) });

        await using var subAgent = world.SessionFor(world.SubAgentId);
        var inherited = await subAgent.Pricing.InheritedRulesAsync();

        // Kept from sub-agents: the principal's own margin, never shown. Ended: prices nothing.
        // The rest in the engine's order, the narrowest first.
        inherited.Select(rule => rule.Id).Should().Equal(flights.Id, global.Id);
    }

    [Fact]
    public async Task A_principal_inherits_no_rules()
    {
        var world = await WorldAsync();
        await world.SeedRuleAsync(world.SubAgentId, Global(percent: 1_000));

        await using var principal = world.SessionFor(world.PrincipalId);

        (await principal.Pricing.InheritedRulesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Settings_say_whether_the_agency_has_a_principal_or_sub_agents()
    {
        var world = await WorldAsync();

        await using var principal = world.SessionFor(world.PrincipalId);
        await using var subAgent = world.SessionFor(world.SubAgentId);

        var principalSettings = await principal.Pricing.SettingsAsync();
        var subAgentSettings = await subAgent.Pricing.SettingsAsync();

        (principalSettings.HasPrincipal, principalSettings.HasSubAgents).Should().Be((false, true));
        (subAgentSettings.HasPrincipal, subAgentSettings.HasSubAgents).Should().Be((true, false));
    }

    [Fact]
    public async Task A_principal_is_not_priced_by_its_sub_agents_rules()
    {
        var world = await WorldAsync();
        await world.SeedRuleAsync(world.SubAgentId, Global(percent: 1_000));

        await using var principal = world.SessionFor(world.PrincipalId);
        var quote = await principal.Pricing.QuoteAsync(Flight, new Money(100_000));

        quote.MarkupRuleId.Should().BeNull();
    }

    // ------------------------------------------------------------------ the cache

    [Fact]
    public async Task Rules_come_from_the_cache_until_one_changes()
    {
        var world = await WorldAsync();
        var global = await world.SeedRuleAsync(world.PrincipalId, Global(percent: 1_000));

        await using var principal = world.SessionFor(world.PrincipalId);
        (await principal.Pricing.PriceAsync(Flight, new Money(100_000))).MarkupRuleId.Should().Be(global.Id);

        // Written straight to the database, around the service, so nothing invalidates. A price
        // that ignores it proves the rules came from Redis rather than PostgreSQL.
        var flights = await world.SeedRuleAsync(world.PrincipalId, ForFlights(percent: 500));

        (await principal.Pricing.PriceAsync(Flight, new Money(100_000))).MarkupRuleId
            .Should().Be(global.Id, "the cached rule set does not know about the new rule yet");

        // Any change through the service invalidates — here, retiring the global rule.
        await principal.Rules.RetireAsync(global.Id);

        (await principal.Pricing.PriceAsync(Flight, new Money(100_000))).MarkupRuleId
            .Should().Be(flights.Id, "the invalidation sent the next read back to the database");
    }

    [Fact]
    public async Task A_new_rule_applies_to_the_very_next_price()
    {
        var world = await WorldAsync();
        await world.SeedRuleAsync(world.PrincipalId, Global(percent: 1_000));

        await using var principal = world.SessionFor(world.PrincipalId);
        await principal.Pricing.PriceAsync(Flight, new Money(100_000));   // warms the cache

        var created = await principal.Rules.CreateAsync(ForFlights(percent: 500));
        var rule = created.Should().BeOfType<MarkupRuleChangeOutcome.Saved>().Subject.Rule;

        (await principal.Pricing.PriceAsync(Flight, new Money(100_000))).MarkupRuleId.Should().Be(rule.Id);
    }

    [Fact]
    public async Task A_principals_rule_change_reaches_its_sub_agents_at_once()
    {
        var world = await WorldAsync();
        var original = await world.SeedRuleAsync(world.PrincipalId, Global(percent: 1_000));

        await using var subAgent = world.SessionFor(world.SubAgentId);
        (await subAgent.Pricing.PriceAsync(Flight, new Money(100_000))).MarkupRuleId.Should().Be(original.Id);

        // The principal edits its rule. Only the principal's own entry is invalidated; the
        // sub-agent reads that entry for inheritance, so it must see the change without waiting
        // for its own entry to expire.
        await using (var principal = world.SessionFor(world.PrincipalId))
        {
            await principal.Rules.ReplaceAsync(original.Id, Global(percent: 2_000));
        }

        var price = await subAgent.Pricing.PriceAsync(Flight, new Money(100_000));

        price.MarkupRuleId.Should().NotBe(original.Id);
        price.MarkupAmountMinor.Should().Be(new Money(20_000));
    }

    [Fact]
    public async Task A_fill_racing_a_rule_change_cannot_hide_the_change()
    {
        var cache = NewCache(_redis.Connection);
        var agencyId = Guid.CreateVersion7();
        var stale = new MarkupRuleSet(agencyId, null, Agency.DefaultVatRateBasisPoints, []);
        var fresh = new MarkupRuleSet(agencyId, null, Agency.DefaultVatRateBasisPoints, [Definition(agencyId)]);

        // Request A reads the old rules; while it is doing so, request B commits a change and
        // invalidates. A then writes what it read. With a plain "delete the key" this stale write
        // would be served until it expired.
        await cache.GetOrLoadAsync(agencyId, async token =>
        {
            await cache.InvalidateAsync(agencyId, token);
            return stale;
        });

        var loads = 0;
        var next = await cache.GetOrLoadAsync(agencyId, _ =>
        {
            loads++;
            return Task.FromResult(fresh);
        });

        loads.Should().Be(1, "the stale write landed under a generation nobody reads any more");
        next.Rules.Should().ContainSingle();
    }

    [Fact]
    public async Task A_second_read_is_served_without_touching_the_loader()
    {
        var cache = NewCache(_redis.Connection);
        var agencyId = Guid.CreateVersion7();
        var set = new MarkupRuleSet(agencyId, Guid.CreateVersion7(), Agency.DefaultVatRateBasisPoints, [Definition(agencyId)]);

        await cache.GetOrLoadAsync(agencyId, _ => Task.FromResult(set));

        var loads = 0;
        var cached = await cache.GetOrLoadAsync(agencyId, _ =>
        {
            loads++;
            return Task.FromResult(set);
        });

        loads.Should().Be(0);

        // And the round trip through JSON kept every term the engine needs.
        cached.ParentAgencyId.Should().Be(set.ParentAgencyId);
        cached.Rules.Should().ContainSingle().Which.Should().BeEquivalentTo(set.Rules[0]);
    }

    [Fact]
    public async Task Pricing_still_works_when_redis_is_unreachable()
    {
        var world = await WorldAsync();
        var global = await world.SeedRuleAsync(world.PrincipalId, Global(percent: 1_000));

        // Nothing listens on port 1. The cache must fall back to the database, not fail the quote:
        // a checkout that dies because a cache is down is revenue the agent never sees again.
        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 250;
        options.AsyncTimeout = 250;
        options.SyncTimeout = 250;
        await using var unreachable = await ConnectionMultiplexer.ConnectAsync(options);

        await using var principal = world.SessionFor(world.PrincipalId, NewCache(unreachable));

        var quote = await principal.Pricing.QuoteAsync(Flight, new Money(100_000));
        quote.MarkupRuleId.Should().Be(global.Id);

        // And a rule change still saves; the failed invalidation is logged, not thrown.
        var created = await principal.Rules.CreateAsync(ForFlights(percent: 500));
        created.Should().BeOfType<MarkupRuleChangeOutcome.Saved>();
    }

    // ------------------------------------------------------------------ helpers

    private static RedisMarkupRuleCache NewCache(IConnectionMultiplexer connection) =>
        new(connection, new MarkupRuleCacheOptions(), NullLogger<RedisMarkupRuleCache>.Instance);

    private static MarkupRuleDefinition Definition(Guid agencyId) =>
        MarkupRule.Create(agencyId, ForFlights(percent: 750) with { MinMarkupMinor = new Money(100), MaxMarkupMinor = new Money(9_999) })
            .ToDefinition();

    private static MarkupRuleTerms Global(int percent) => new()
    {
        Scope = MarkupScope.Global,
        Currency = "NGN",
        CalculationType = MarkupCalculationType.Percentage,
        PercentBasisPoints = percent,
        EffectiveFrom = DateTimeOffset.UnixEpoch,
    };

    private static MarkupRuleTerms ForFlights(int percent) => Global(percent) with
    {
        Scope = MarkupScope.ProductType,
        ProductType = PricedProductType.Flight,
    };

    private async Task<World> WorldAsync()
    {
        // A fresh name every time. See RegistrationEndToEndTests: a recreated database reuses
        // nothing, but Npgsql's cached type catalogue for a reused name would.
        var database = $"pricing_{Guid.NewGuid():N}";

        Guid principalId;
        Guid subAgentId;

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(database))
        {
            await setup.Database.MigrateAsync();

            var principal = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            setup.Agencies.Add(principal);
            await setup.SaveChangesAsync();

            var subAgent = Agency.RegisterSubAgent(principal, "Lagos Travel Ikeja Limited", "lagos-travel-ikeja");
            setup.Agencies.Add(subAgent);
            await setup.SaveChangesAsync();

            principalId = principal.Id;
            subAgentId = subAgent.Id;
        }

        return new World(_postgres, database, principalId, subAgentId, NewCache(_redis.Connection));
    }

    private sealed class World
    {
        private readonly PostgresFixture _postgres;
        private readonly string _database;
        private readonly RedisMarkupRuleCache _cache;

        public World(PostgresFixture postgres, string database, Guid principalId, Guid subAgentId, RedisMarkupRuleCache cache)
        {
            _postgres = postgres;
            _database = database;
            _cache = cache;
            PrincipalId = principalId;
            SubAgentId = subAgentId;
        }

        public Guid PrincipalId { get; }

        public Guid SubAgentId { get; }

        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero));

        /// <summary>The services as a request from <paramref name="agencyId"/> would get them.</summary>
        public Session SessionFor(Guid agencyId, IMarkupRuleCache? cache = null, TimeSpan? validity = null)
        {
            var tenancy = TestTenancy.For(agencyId);
            var db = _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope, Clock);
            var chosen = cache ?? _cache;
            var quoteOptions = validity is { } minutes ? new PriceQuoteOptions { Validity = minutes } : new PriceQuoteOptions();

            return new Session(
                db,
                new PricingService(db, tenancy.Tenant, tenancy.Scope, chosen, new NoPlatformFeePolicy(), quoteOptions, Clock),
                new MarkupRuleService(db, tenancy.Tenant, chosen, Clock));
        }

        /// <summary>
        /// Changes an agency's VAT rate — before anything is cached for it, so the first price reads it.
        /// </summary>
        /// <remarks>
        /// Raw SQL as the owner. Through EF the agency filter would need a tenant to find the row, and
        /// nothing in the product changes a VAT rate yet, so there is no service to go through.
        /// </remarks>
        public async Task SetVatRateAsync(Guid agencyId, int basisPoints)
        {
            await using var owner = _postgres.Connect(_database, asApplicationRole: false);
            await owner.Database.ExecuteSqlRawAsync(
                "UPDATE tenancy.agencies SET vat_rate_basis_points = {0} WHERE id = {1}", basisPoints, agencyId);
        }

        /// <summary>
        /// Writes a rule straight to the database, around <see cref="MarkupRuleService"/> — so the
        /// cache is <b>not</b> told. Tests that care about invalidation rely on exactly that.
        /// </summary>
        public async Task<MarkupRule> SeedRuleAsync(Guid agencyId, MarkupRuleTerms terms)
        {
            var tenancy = TestTenancy.For(agencyId);
            await using var db = _postgres.Connect(_database, tenancy.Tenant, tenancy.Scope, Clock);

            var rule = MarkupRule.Create(agencyId, terms);
            db.MarkupRules.Add(rule);
            await db.SaveChangesAsync();

            return rule;
        }
    }

    private sealed record Session(AppDbContext Db, PricingService Pricing, MarkupRuleService Rules) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
