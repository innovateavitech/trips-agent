using FluentAssertions;
using TripsAgent.Application.RateLimiting;

namespace TripsAgent.UnitTests.RateLimiting;

/// <summary>
/// The decision, with the store faked. Redis itself — and two API instances agreeing through it — is
/// proved in the integration tests; these pin down the arithmetic and the failure behaviour.
/// </summary>
public class RateLimitEvaluatorTests
{
    private static readonly RateLimitCaller Anonymous = new("ip:203.0.113.7", AgencyPartition: null);

    [Fact]
    public async Task Requests_up_to_the_limit_are_allowed_and_the_next_one_is_refused()
    {
        var evaluator = Evaluator(new CountingStore(), login: new RateLimitRule(3, TimeSpan.FromMinutes(5)));

        var decisions = new List<RateLimitDecision>();
        for (var i = 0; i < 4; i++)
        {
            decisions.Add(await evaluator.EvaluateAsync(RateLimitPolicyNames.Login, Anonymous));
        }

        decisions.Take(3).Should().OnlyContain(decision => decision.Allowed);
        decisions.Select(decision => decision.Remaining).Should().Equal(2, 1, 0, 0);

        var refused = decisions[3];
        refused.Allowed.Should().BeFalse();
        refused.Limit.Should().Be(3);
        refused.Policy.Should().Be(RateLimitPolicyNames.Login);
        refused.Partition.Should().Be("ip:203.0.113.7", "a rejection has to name who was refused, or abuse is invisible");
    }

    [Fact]
    public async Task A_refusal_says_when_the_window_ends()
    {
        var store = new CountingStore { ResetsIn = TimeSpan.FromSeconds(42) };
        var evaluator = Evaluator(store, login: new RateLimitRule(1, TimeSpan.FromMinutes(5)));

        await evaluator.EvaluateAsync(RateLimitPolicyNames.Login, Anonymous);
        var refused = await evaluator.EvaluateAsync(RateLimitPolicyNames.Login, Anonymous);

        refused.Allowed.Should().BeFalse();
        refused.ResetsIn.Should().Be(TimeSpan.FromSeconds(42));
        refused.Window.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Each_policy_counts_separately_so_browsing_does_not_use_up_sign_in_attempts()
    {
        var store = new CountingStore();
        var evaluator = Evaluator(store, login: new RateLimitRule(1, TimeSpan.FromMinutes(5)));

        await evaluator.EvaluateAsync(RateLimitPolicyNames.Default, Anonymous);
        await evaluator.EvaluateAsync(RateLimitPolicyNames.Default, Anonymous);

        (await evaluator.EvaluateAsync(RateLimitPolicyNames.Login, Anonymous)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_signed_in_request_is_counted_against_both_its_user_and_its_agency()
    {
        var store = new CountingStore();
        var caller = new RateLimitCaller("user:ada", "agency:lagos");

        await Evaluator(store).EvaluateAsync(RateLimitPolicyNames.Default, caller);

        store.Keys.Should().BeEquivalentTo(
            RateLimitEvaluator.KeyFor(RateLimitPolicyNames.Default, "user:ada"),
            RateLimitEvaluator.KeyFor(RateLimitPolicyNames.Agency, "agency:lagos"));
    }

    [Fact]
    public async Task The_agency_ceiling_refuses_a_user_who_is_still_under_their_own_limit()
    {
        var store = new CountingStore();
        var evaluator = Evaluator(
            store,
            @default: new RateLimitRule(10, TimeSpan.FromMinutes(1)),
            agency: new RateLimitRule(3, TimeSpan.FromMinutes(1)));

        // Three colleagues use up the agency's allowance between them.
        await evaluator.EvaluateAsync(RateLimitPolicyNames.Default, new RateLimitCaller("user:a", "agency:lagos"));
        await evaluator.EvaluateAsync(RateLimitPolicyNames.Default, new RateLimitCaller("user:b", "agency:lagos"));
        await evaluator.EvaluateAsync(RateLimitPolicyNames.Default, new RateLimitCaller("user:c", "agency:lagos"));

        var fourth = await evaluator.EvaluateAsync(RateLimitPolicyNames.Default, new RateLimitCaller("user:d", "agency:lagos"));
        var otherAgency = await evaluator.EvaluateAsync(RateLimitPolicyNames.Default, new RateLimitCaller("user:e", "agency:abuja"));

        fourth.Allowed.Should().BeFalse("user d has made one request, but their agency is over its ceiling");
        fourth.Policy.Should().Be(RateLimitPolicyNames.Agency);
        fourth.Partition.Should().Be("agency:lagos");

        otherAgency.Allowed.Should().BeTrue("one agency's traffic must never use up another's");
    }

    [Fact]
    public async Task The_headers_report_whichever_bucket_is_nearest_its_limit()
    {
        var store = new CountingStore();
        var evaluator = Evaluator(
            store,
            @default: new RateLimitRule(100, TimeSpan.FromMinutes(1)),
            agency: new RateLimitRule(5, TimeSpan.FromMinutes(1)));

        var decision = await evaluator.EvaluateAsync(RateLimitPolicyNames.Default, new RateLimitCaller("user:a", "agency:lagos"));

        decision.Allowed.Should().BeTrue();
        decision.Policy.Should().Be(RateLimitPolicyNames.Agency);
        decision.Limit.Should().Be(5);
        decision.Remaining.Should().Be(4);
    }

    [Fact]
    public async Task An_unreachable_store_lets_the_request_through_uncounted()
    {
        // Refusing everything instead would turn a Redis outage into an outage of the whole API.
        var evaluator = Evaluator(new CountingStore { Unavailable = true }, login: new RateLimitRule(1, TimeSpan.FromMinutes(5)));

        for (var i = 0; i < 5; i++)
        {
            var decision = await evaluator.EvaluateAsync(RateLimitPolicyNames.Login, Anonymous);

            decision.Allowed.Should().BeTrue();
            decision.Counted.Should().BeFalse("nothing was counted, so there are no numbers to report");
        }
    }

    [Fact]
    public async Task A_store_that_answers_for_the_user_but_not_the_agency_still_fails_open()
    {
        // Half an answer is not an answer: the missing bucket might be the one that should refuse.
        var store = new CountingStore { UnavailableKeyContains = "agency:" };

        var decision = await Evaluator(store).EvaluateAsync(RateLimitPolicyNames.Default, new RateLimitCaller("user:a", "agency:lagos"));

        decision.Allowed.Should().BeTrue();
        decision.Counted.Should().BeFalse();
    }

    [Fact]
    public async Task Keys_are_prefixed_so_they_can_be_found_and_left_alone()
    {
        var store = new CountingStore();

        await Evaluator(store).EvaluateAsync(RateLimitPolicyNames.Login, Anonymous);

        store.Keys.Should().ContainSingle().Which.Should().Be("ratelimit:Login:ip:203.0.113.7");
    }

    [Fact]
    public async Task An_unknown_policy_is_a_programming_error_not_an_unlimited_endpoint()
    {
        var act = () => Evaluator(new CountingStore()).EvaluateAsync("Logn", Anonymous);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Logn*");
    }

    private static RateLimitEvaluator Evaluator(
        IRateLimitStore store,
        RateLimitRule? login = null,
        RateLimitRule? @default = null,
        RateLimitRule? agency = null)
    {
        var rules = new Dictionary<string, RateLimitRule>(RateLimitSettings.Defaults, StringComparer.Ordinal);

        if (login is not null)
        {
            rules[RateLimitPolicyNames.Login] = login;
        }

        if (@default is not null)
        {
            rules[RateLimitPolicyNames.Default] = @default;
        }

        if (agency is not null)
        {
            rules[RateLimitPolicyNames.Agency] = agency;
        }

        return new RateLimitEvaluator(store, new RateLimitSettings(enabled: true, rules));
    }

    /// <summary>A store that counts in a dictionary — exactly what production must never do, which is why it is only here.</summary>
    private sealed class CountingStore : IRateLimitStore
    {
        private readonly Dictionary<string, long> _hits = new(StringComparer.Ordinal);

        public bool Unavailable { get; init; }

        public string? UnavailableKeyContains { get; init; }

        public TimeSpan? ResetsIn { get; init; }

        public IReadOnlyCollection<string> Keys => _hits.Keys;

        public Task<RateLimitWindowCount?> HitAsync(string key, TimeSpan window, CancellationToken cancellationToken = default)
        {
            if (Unavailable || (UnavailableKeyContains is not null && key.Contains(UnavailableKeyContains, StringComparison.Ordinal)))
            {
                return Task.FromResult<RateLimitWindowCount?>(null);
            }

            var hits = _hits.TryGetValue(key, out var previous) ? previous + 1 : 1;
            _hits[key] = hits;

            return Task.FromResult<RateLimitWindowCount?>(new RateLimitWindowCount(hits, ResetsIn ?? window));
        }
    }
}
