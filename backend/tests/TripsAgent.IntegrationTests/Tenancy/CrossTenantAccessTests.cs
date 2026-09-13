using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Checkout;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Tenancy.SubAgents;
using TripsAgent.Domain.Assets;
using TripsAgent.Domain.Common;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Orders;
using TripsAgent.Domain.Payments;
using TripsAgent.Domain.Pricing;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Domain.Tenancy.SubAgents;
using TripsAgent.Infrastructure.Persistence;
using TripsAgent.IntegrationTests.Identity;
using TripsAgent.IntegrationTests.Payments;
using TripsAgent.IntegrationTests.Persistence;
using TripsAgent.IntegrationTests.Suppliers;

namespace TripsAgent.IntegrationTests.Tenancy;

/// <summary>
/// One agency, holding another agency's id, asking for that other agency's row — across the four
/// endpoint groups an internal adversarial pass found had no such test. Issue 110.
/// </summary>
/// <remarks>
/// <para>
/// The other tenancy tests in this folder ask "what does an agency see when it lists its own
/// rows?", which is the honest caller's question. A penetration tester asks a different one: they
/// take an id out of one agency's screen and paste it into another agency's session. That is a
/// direct object reference, and every one of these four areas already looks up by id — a bank
/// account, a dispute, an order number, a sub-agency — so every one of them is a place the filter
/// could be forgotten without a single list view looking wrong.
/// </para>
/// <para>
/// Each test asks for the same three things, because "refused" on its own is not enough:
/// </para>
/// <list type="number">
/// <item>the caller is told <b>not found</b>, and not "forbidden" — a 403 on somebody else's id
/// confirms the id is real, which is half of what the tester came for;</item>
/// <item>none of the other agency's data comes back with the refusal;</item>
/// <item>nothing was written — the refusal happened <i>before</i> the mutation, not after it.</item>
/// </list>
/// <para>
/// Every test also does the same thing with the caller's <i>own</i> id and expects it to work. A
/// test that only proves a refusal would still pass if the feature were broken for everybody, and
/// would then be quietly protecting nothing.
/// </para>
/// <para>
/// Nothing here uses <c>IgnoreQueryFilters</c>. Where a test has to look at the other agency's row
/// to prove it was left alone, it opens the platform scope, the same way production reads across
/// agencies — see ADR-0006.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class CrossTenantAccessTests
{
    private readonly PostgresFixture _postgres;

    public CrossTenantAccessTests(PostgresFixture postgres) => _postgres = postgres;

    // ------------------------------------------------------------------ payouts

    [Fact]
    public async Task Another_agencys_bank_account_cannot_be_made_default_or_removed()
    {
        await using var world = await PayoutWorldAsync();
        var other = await SeedSecondAgencyAsync(world);

        // Both calls take a bank account id straight from the URL, so this is the whole attack:
        // read an id off one agency's payout screen, send it from another agency's session.
        (await world.BankAccounts().MakeDefaultAsync(other.BankAccountId))
            .Should().BeFalse("a bank account id from another agency has to look like one that does not exist");

        (await world.BankAccounts().RemoveAsync(other.BankAccountId)).Should().BeFalse();

        // Nothing moved. Making somebody else's account the default would also have cleared ours,
        // so this checks both sides of the same write.
        using (world.Tenancy.Scope.Enter("test — reading the other agency's bank account to prove it was left alone"))
        {
            world.Db.ChangeTracker.Clear();

            var theirs = await world.Db.AgencyBankAccounts.AsNoTracking().SingleAsync(a => a.Id == other.BankAccountId);
            theirs.IsDefault.Should().BeFalse();
            theirs.Status.Should().Be(BankAccountStatus.Verified, "a refusal must not retire another agency's account");

            var ours = await world.Db.AgencyBankAccounts.AsNoTracking().SingleAsync(a => a.Id == world.BankAccountId);
            ours.IsDefault.Should().BeTrue();
        }

        // The control: the same call with our own id works, so the refusals above are about whose
        // account it is and not about the feature being broken.
        world.Db.ChangeTracker.Clear();
        (await world.BankAccounts().MakeDefaultAsync(world.BankAccountId)).Should().BeTrue();
    }

    [Fact]
    public async Task A_payout_cannot_be_sent_to_another_agencys_bank_account()
    {
        await using var world = await PayoutWorldAsync();
        var other = await SeedSecondAgencyAsync(world);
        await world.FundSettledAsync(2_000_000);

        // The most valuable version of this bug: our money, their account. The destination is
        // chosen by an id in the request body, and the other agency's account is verified and past
        // its cooling-off period, so nothing but the tenant filter stands between the two.
        var outcome = await world.Payouts().RequestAsync(new Money(1_000_000), other.BankAccountId);

        outcome.Should().BeOfType<RequestPayoutOutcome.DestinationNotReady>(
            "an account belonging to another agency is not a destination this agency has");

        (await world.Db.Payouts.CountAsync()).Should().Be(0, "the refusal happened before anything was recorded");
        (await world.WalletAsync()).BalanceMinor.AmountMinor.Should().Be(2_000_000);

        // The control: our own account is a destination, and the same amount goes through.
        world.Db.ChangeTracker.Clear();
        (await world.Payouts().RequestAsync(new Money(1_000_000), world.BankAccountId))
            .Should().BeOfType<RequestPayoutOutcome.Requested>();
    }

    // ------------------------------------------------------------------ disputes

    [Fact]
    public async Task Another_agencys_dispute_cannot_be_opened_or_answered()
    {
        await using var world = await PayoutWorldAsync();
        var other = await SeedSecondAgencyAsync(world);

        // GET /api/v1/disputes/{disputeId} is an inline handler over this exact query, so asking
        // the same question of the same context is the endpoint's own lookup.
        var read = await world.Db.Disputes.AsNoTracking().FirstOrDefaultAsync(d => d.Id == other.DisputeId);

        read.Should().BeNull("a dispute id from another agency must read as though there were no such dispute");
        (await world.Db.Disputes.CountAsync()).Should().Be(0, "this agency has no disputes of its own");

        // Filing evidence is worse than reading it: the cardholder's name, email and phone number
        // would be sent to the gateway under another agency's chargeback, and the deadline that
        // agency is relying on would be spent.
        var evidence = new EvidenceSubmission(
            "Tunde Bello", "tunde@example.com", "+2348000000000", "Lagos–Abuja flight", null, null, null);

        (await world.Disputes().SubmitEvidenceAsync(other.DisputeId, evidence))
            .Should().Be(SubmitEvidenceOutcome.NotFound);

        world.BackOffice.Evidence.Should().BeEmpty("nothing may reach the gateway on another agency's behalf");

        using (world.Tenancy.Scope.Enter("test — reading the other agency's dispute to prove it was left alone"))
        {
            world.Db.ChangeTracker.Clear();

            var theirs = await world.Db.Disputes.AsNoTracking().SingleAsync(d => d.Id == other.DisputeId);
            theirs.EvidenceSubmittedAt.Should().BeNull();
            theirs.Status.Should().Be(DisputeStatus.Open, "the row is really there — the refusal above was the filter, not an empty table");
        }
    }

    [Fact]
    public async Task Evidence_cannot_be_filed_with_another_agencys_file_attached()
    {
        await using var world = await PayoutWorldAsync();
        var other = await SeedSecondAgencyAsync(world);
        var disputeId = await OpenOurDisputeAsync(world);
        var ours = await SeedOurAssetAsync(world);

        var evidence = new EvidenceSubmission(
            "Tunde Bello", "tunde@example.com", "+2348000000000", "Lagos–Abuja flight", null, null, [other.AssetId]);

        // An asset id is the one part of a submission the agent chooses by id, so it is the one part
        // that can name another agency's row (issue 175).
        (await world.Disputes().SubmitEvidenceAsync(disputeId, evidence))
            .Should().Be(SubmitEvidenceOutcome.UnknownAsset);

        world.BackOffice.Evidence.Should().BeEmpty("nothing reaches the gateway until every file is ours");

        world.Db.ChangeTracker.Clear();
        (await world.Db.Disputes.AsNoTracking().SingleAsync(d => d.Id == disputeId))
            .EvidenceSubmittedAt.Should().BeNull("the refusal happened before anything was recorded");

        // One of ours and one of theirs is still theirs: a mixed list is refused whole.
        (await world.Disputes().SubmitEvidenceAsync(disputeId, evidence with { AssetIds = [ours, other.AssetId] }))
            .Should().Be(SubmitEvidenceOutcome.UnknownAsset);

        // The control: this agency's own file goes through, and is what gets recorded.
        world.Db.ChangeTracker.Clear();
        (await world.Disputes().SubmitEvidenceAsync(disputeId, evidence with { AssetIds = [ours] }))
            .Should().Be(SubmitEvidenceOutcome.Submitted);

        world.BackOffice.Evidence.Should().ContainSingle();

        world.Db.ChangeTracker.Clear();
        (await world.Db.Disputes.AsNoTracking().SingleAsync(d => d.Id == disputeId))
            .EvidenceAssetIds.Should().Contain(ours.ToString());
    }

    // ------------------------------------------------------------------ bookings

    [Fact]
    public async Task Another_agencys_booking_cannot_be_opened_followed_or_refunded()
    {
        await using var stub = await TripsAfricaStub.StartAsync();
        await using var harness = await BookingPipelineHarness.CreateAsync(_postgres, stub);

        var ours = await harness.SeedConfirmedBookingAsync(paidFromWallet: true);
        var theirReference = await SeedOtherAgencysFailedBookingAsync(harness);

        var seen = await harness.InAgencyScopeAsync(async provider =>
        {
            var bookings = provider.GetRequiredService<BookingQueries>();

            return (
                Ours: await bookings.FindAsync(ours.OrderNumber),
                Theirs: await bookings.FindAsync(theirReference),
                TheirProgress: await bookings.ProgressAsync(theirReference),
                List: await bookings.ListAsync());
        });

        seen.Ours.Should().NotBeNull("the control: this agency can open its own booking");
        seen.Theirs.Should().BeNull("an order number belonging to another agency is not this agency's to open");
        seen.TheirProgress.Should().BeNull("the progress poll is the same lookup and must answer the same way");
        seen.List.Select(booking => booking.Reference).Should().Equal(ours.OrderNumber);

        // Resolving is the one that spends money: the other agency's line is failed and waiting for
        // a decision, so a missing filter here would refund their traveller out of their wallet on
        // our say-so. A 404 rather than a 403, deliberately — see the remarks on this class.
        var resolve = () => harness.ResolveAsync(theirReference, ResolutionChoice.Refund);

        (await resolve.Should().ThrowAsync<CheckoutRefusedException>())
            .Which.Refusal.Should().Be(CheckoutRefusal.NotFound);

        await using var owner = OwnerContext(harness.Database, harness.Clock, out var platform);
        using (platform.Scope.Enter("test — reading the other agency's booking to prove nothing was refunded"))
        {
            var theirs = await owner.Orders.AsNoTracking()
                .Include(order => order.Lines)
                .SingleAsync(order => order.OrderNumber == theirReference);

            theirs.Status.Should().NotBe(OrderStatus.Refunded);
            theirs.Lines[0].ResolutionStatus.Should().Be(ResolutionStatus.Open, "their booking is still waiting for their decision");
            (await owner.Refunds.CountAsync()).Should().Be(0);
        }
    }

    // ------------------------------------------------------------------ the sub-agent network

    [Fact]
    public async Task Another_networks_sub_agent_tells_a_principal_nothing()
    {
        var (world, theirSubAgent) = await NetworkAsync();
        await using var _ = world;
        await using var session = world.ActingAs(world.Principal);

        var scopes = new SubAgentScopeService(session.Db, session.Tenant);
        var permissions = new SubAgentPermissionService(session.Db, session.Tenant);
        var allowances = new SubAgentAllowanceService(session.Db, session.Tenant, Clock());

        // What another principal may sell, what it has been denied and what it may spend are that
        // network's commercial terms. Reading them would tell a competitor how the rival network
        // is run, from nothing but a sub-agency id.
        await ShouldBeNotFoundAsync(() => scopes.ListAsync(theirSubAgent));
        await ShouldBeNotFoundAsync(() => permissions.MatrixAsync(theirSubAgent));

        // The allowance read is the odd one out: it answers with null rather than refusing,
        // because it is also the call a sub-agent makes about itself. Null is still the right
        // answer here — it says no allowance of ours is about that agency.
        (await allowances.GetAsync(theirSubAgent)).Should().BeNull();

        // The control: all three work for a sub-agent that really is ours.
        (await scopes.ListAsync(world.SubAgent)).Should().ContainSingle();
        (await permissions.MatrixAsync(world.SubAgent)).Should().Contain(row => row.IsDenied);
        (await allowances.GetAsync(world.SubAgent))!.LimitMinor.Should().Be(1_000_000);
    }

    [Fact]
    public async Task Another_networks_sub_agent_cannot_be_rescoped_denied_or_capped()
    {
        var (world, theirSubAgent) = await NetworkAsync();
        await using var _ = world;
        await using var session = world.ActingAs(world.Principal);

        var scopes = new SubAgentScopeService(session.Db, session.Tenant);
        var permissions = new SubAgentPermissionService(session.Db, session.Tenant);
        var allowances = new SubAgentAllowanceService(session.Db, session.Tenant, Clock());

        // Each of these is a write against an agency in somebody else's network. Denying a
        // permission is the quietest and the nastiest: it takes a capability away from another
        // principal's staff, and the agency it happens to has no way of knowing who did it.
        await ShouldBeNotFoundAsync(() => scopes.GrantAsync(theirSubAgent, SellableProductType.Bus, null));
        await ShouldBeNotFoundAsync(() => permissions.DenyAsync(theirSubAgent, PermissionCodes.BookingCreate, "not mine to take"));
        await ShouldBeNotFoundAsync(() => allowances.SetAsync(theirSubAgent, 9_999_999, AllowancePeriod.Monthly));
        await ShouldBeNotFoundAsync(() => allowances.FreezeAsync(theirSubAgent));

        // Counted as the database owner, so the assertion is about the rows and not about what any
        // one agency's filter shows. Each was seeded with exactly one row, and each still has it.
        (await world.AdminCountAsync(
                $"SELECT count(*) FROM tenancy.sub_agent_scopes WHERE sub_agency_id = '{theirSubAgent}'"))
            .Should().Be(1, "granting Bus would have made it two");

        (await world.AdminCountAsync(
                $"SELECT count(*) FROM tenancy.permission_overrides WHERE sub_agency_id = '{theirSubAgent}'"))
            .Should().Be(1);

        (await world.AdminCountAsync(
                $"SELECT limit_minor FROM payments.wallet_allowances WHERE sub_agency_id = '{theirSubAgent}'"))
            .Should().Be(1_000_000, "their cap is their principal's to set");

        // The control: the same three writes land on a sub-agent that really is ours.
        await scopes.GrantAsync(world.SubAgent, SellableProductType.Bus, null);
        await permissions.DenyAsync(world.SubAgent, PermissionCodes.BookingCreate, "Branches do not book direct.");
        await allowances.SetAsync(world.SubAgent, 2_000_000, AllowancePeriod.Monthly);

        (await world.AdminCountAsync(
                $"SELECT count(*) FROM tenancy.sub_agent_scopes WHERE sub_agency_id = '{world.SubAgent}'"))
            .Should().Be(2);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A clock the sub-agent services can read. None of these tests turn it.</summary>
    private static ManualClock Clock() => new(new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero));

    /// <summary>Asserts a sub-agent call was refused with "no such sub-agent", not "you may not".</summary>
    private static async Task ShouldBeNotFoundAsync(Func<Task> call)
    {
        (await call.Should().ThrowAsync<SubAgentRefusedException>())
            .Which.Refusal.Should().Be(
                SubAgentRefusal.NotFound,
                "a 403 on another network's id would confirm the id is real");
    }

    private Task<PayoutWorld> PayoutWorldAsync([CallerMemberName] string testName = "") =>
        PayoutWorld.CreateAsync(_postgres, testName);

    /// <summary>The other agency's ids, for the payout and dispute tests.</summary>
    private sealed record OtherAgency(Guid AgencyId, Guid BankAccountId, Guid DisputeId, Guid AssetId);

    /// <summary>A chargeback of this agency's own, opened the way the webhook opens one.</summary>
    private static async Task<Guid> OpenOurDisputeAsync(PayoutWorld world)
    {
        var payment = await world.PayInAsync(1_000_000);
        var opened = world.Clock.GetUtcNow();

        world.BackOffice.Disputes["D-OURS"] = new GatewayDispute(
            "D-OURS", payment.Reference, new Money(100_000), "NGN", GatewayDisputeState.Open,
            "awaiting-merchant-feedback", null, "chargeback", "I do not recognise this charge",
            opened, opened.AddDays(3));

        await world.Disputes().SyncAsync("D-OURS");
        world.Db.ChangeTracker.Clear();

        return (await world.Db.Disputes.AsNoTracking().SingleAsync()).Id;
    }

    /// <summary>A file of this agency's own, as the console's upload would have left it.</summary>
    private static async Task<Guid> SeedOurAssetAsync(PayoutWorld world)
    {
        var asset = Asset.Reserve(
            world.AgencyId, AssetPurpose.Attachment, "boarding-pass.pdf", world.Clock.GetUtcNow().AddMinutes(15));

        world.Db.Assets.Add(asset);
        await world.Db.SaveChangesAsync();

        return asset.Id;
    }

    /// <summary>
    /// A second verified agency inside <see cref="PayoutWorld"/>'s database, with a bank account
    /// ready to receive money and an open chargeback.
    /// </summary>
    /// <remarks>
    /// Seeded here rather than in <see cref="PayoutWorld"/> because every other test in that file
    /// is about one agency, and a second one would change what "the wallet" and "the bank account"
    /// mean in all of them. Both rows are deliberately in their most reachable state — verified,
    /// past the cooling-off period, still accepting evidence — so that the only thing refusing the
    /// calls below is the tenant filter.
    /// </remarks>
    private static async Task<OtherAgency> SeedSecondAgencyAsync(PayoutWorld world)
    {
        var setup = TestTenancy.None();
        var db = world.NewContext(setup.Tenant, setup.Scope);
        var now = world.Clock.GetUtcNow();

        using var _ = setup.Scope.Enter("test setup — a second agency, whose rows this test must not be able to touch");

        var agency = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");
        agency.MarkVerified(now);
        db.Agencies.Add(agency);
        db.Wallets.Add(Wallet.OpenFor(agency.Id, "NGN"));

        var owner = User.ForAgency(agency.Id, $"owner-{Guid.NewGuid():N}@abujatours.example.com", "hash", "Chidi", "Eze");
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var account = AgencyBankAccount.Capture(agency.Id, "044", "Access Bank", "9876543210", "Abuja Tours", "NGN", owner.Id);
        account.MarkVerified("ABUJA TOURS LIMITED", "RCP_other", now.AddDays(-2));
        db.AgencyBankAccounts.Add(account);

        var payment = PaymentTransaction.Start(
            agency.Id, owner.Id, PaymentPurpose.WalletTopUp, new Money(1_000_000), "NGN", $"TU-{Guid.NewGuid():N}");
        payment.MarkSucceeded(new Money(1_000_000), new Money(1_500), "NGN", payment.Reference, now);
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();

        var dispute = Dispute.Open(
            agency.Id, payment.Id, payment.Reference, $"D-{Guid.NewGuid():N}", new Money(400_000), "NGN",
            "chargeback", "I do not recognise this charge", now, now.AddDays(3), orderId: null);

        db.Disputes.Add(dispute);

        // A file of theirs. An asset id is the one part of an evidence submission an agent chooses by
        // id, so it is the one part that can name a row belonging to somebody else.
        var asset = Asset.Reserve(agency.Id, AssetPurpose.Attachment, "their-invoice.pdf", now.AddMinutes(15));
        db.Assets.Add(asset);

        await db.SaveChangesAsync();

        return new OtherAgency(agency.Id, account.Id, dispute.Id, asset.Id);
    }

    /// <summary>
    /// A second agency inside the booking harness's database, with a paid booking whose ticket
    /// failed and which is waiting in that agency's resolution queue.
    /// </summary>
    /// <returns>Its order number — the reference a caller would paste into the URL.</returns>
    /// <remarks>
    /// Failed and unresolved on purpose. A booking in any other state would be refused by the
    /// resolution service for a second reason ("this no longer needs a decision"), and the test
    /// could then pass without the tenant filter doing anything at all.
    /// </remarks>
    private async Task<string> SeedOtherAgencysFailedBookingAsync(BookingPipelineHarness harness)
    {
        const string reference = "ORD-2026-900001";

        await using var db = OwnerContext(harness.Database, harness.Clock, out var platform);
        using var _ = platform.Scope.Enter("test setup — a second agency with a failed booking of its own");

        var now = harness.Clock.GetUtcNow();

        var agency = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");
        agency.MarkVerified(now);
        db.Agencies.Add(agency);
        await db.SaveChangesAsync();

        var rule = MarkupRule.Create(agency.Id, new MarkupRuleTerms
        {
            Scope = MarkupScope.Global,
            Currency = "NGN",
            CalculationType = MarkupCalculationType.Percentage,
            PercentBasisPoints = 1_000,
            EffectiveFrom = now.AddDays(-1),
        });
        db.MarkupRules.Add(rule);
        await db.SaveChangesAsync();

        var quote = PriceQuote.Record(
            agency.Id,
            new PricingSubject(PricedProductType.Flight, "NGN"),
            new PriceBreakdown(
                new Money(100_000), new Money(10_000), new Money(750), new Money(500), new Money(110_750),
                "NGN", new MarkupRuleDefinition(rule.Id, agency.Id, rule.Terms), false, 750, 0),
            now,
            TimeSpan.FromMinutes(30));
        db.PriceQuotes.Add(quote);
        await db.SaveChangesAsync();

        var line = OrderLine.FromQuote(quote, "ABV → LOS, Air Peace", """{"adults":1}""", now);
        var order = Order.Place(agency.Id, reference, "NGN", BuyerType.AgentAssisted, OrderChannel.Console, null, [line], now);

        order.RecordPayment(OrderPaymentMethod.Wallet, $"pay:{line.Id:N}", now);
        line.RecordFulfilment(FulfilmentStatus.FailedNeedsResolution, now, "The supplier did not issue the ticket.");

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        return reference;
    }

    /// <summary>
    /// A context as the schema owner, with a platform scope the caller can open.
    /// </summary>
    /// <remarks>
    /// <see cref="BookingPipelineHarness.AsOwner"/> builds its own tenancy internally, so there is
    /// no scope to open and cross-agency rows stay invisible even to the owner's context. This
    /// hands the scope back so setup and verification can read across both agencies on purpose,
    /// and say why in the log.
    /// </remarks>
    private AppDbContext OwnerContext(
        string database,
        TimeProvider clock,
        out (Application.Tenancy.TenantContext Tenant, Infrastructure.Tenancy.PlatformScope Scope) platform)
    {
        platform = TestTenancy.None();
        return _postgres.Connect(database, platform.Tenant, platform.Scope, clock, asApplicationRole: false);
    }

    /// <summary>
    /// Two unrelated principals, each with a sub-agent of its own, and the terms to prove it.
    /// </summary>
    /// <remarks>
    /// <see cref="SubAgentWorld"/>'s own tests seed one network and an outsider with nothing under
    /// it, which answers "can a stranger read this network?". The question here is the other way
    /// round — a principal reaching <i>into</i> a network that is not its own — so the outsider
    /// needs a sub-agent, and that sub-agent needs a scope, an override and an allowance, or a
    /// leak would have nothing to leak.
    /// </remarks>
    /// <returns>The world, and the id of the sub-agent belonging to the other principal.</returns>
    private async Task<(SubAgentWorld World, Guid TheirSubAgent)> NetworkAsync(
        [CallerMemberName] string testName = "")
    {
        var name = $"xtenant_net_{testName.ToLowerInvariant()}";
        name = name[..Math.Min(name.Length, 60)];

        var tenancy = TestTenancy.None();
        var now = new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);

        await using var setup = await _postgres.CreateEmptyDatabaseAsync(name, tenancy.Tenant, tenancy.Scope);
        await setup.Database.MigrateAsync();

        using var _ = tenancy.Scope.Enter("test setup — two principals, each with a sub-agent and terms of its own");

        var principal = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
        var outsider = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");
        principal.MarkVerified(now);
        outsider.MarkVerified(now);

        setup.Agencies.AddRange(principal, outsider);
        await setup.SaveChangesAsync();

        var subAgent = Agency.RegisterSubAgent(principal, "Ikeja Travel Limited", "ikeja-travel");
        var sibling = Agency.RegisterSubAgent(principal, "Yaba Travel Limited", "yaba-travel");
        var theirs = Agency.RegisterSubAgent(outsider, "Wuse Travel Limited", "wuse-travel");
        subAgent.MarkVerified(now);
        sibling.MarkVerified(now);
        theirs.MarkVerified(now);

        setup.Agencies.AddRange(subAgent, sibling, theirs);
        await setup.SaveChangesAsync();

        foreach (var (owner, under) in new[] { (principal, subAgent), (outsider, theirs) })
        {
            setup.SubAgentScopes.Add(SubAgentScope.Grant(owner.Id, under.Id, SellableProductType.Flight));

            setup.PermissionOverrides.Add(PermissionOverride.Deny(
                owner.Id, under.Id, PermissionCodes.MarginView, "Resellers do not see our net rates."));

            setup.WalletAllowances.Add(WalletAllowance.Open(
                owner.Id, under.Id, "NGN", new Money(1_000_000), AllowancePeriod.Monthly, now));
        }

        await setup.SaveChangesAsync();

        return (new SubAgentWorld(_postgres, name, principal.Id, subAgent.Id, sibling.Id, outsider.Id), theirs.Id);
    }
}
