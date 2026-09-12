using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Api.Crm;
using TripsAgent.Application.Identity;
using TripsAgent.Contracts.Crm;
using TripsAgent.Domain.Identity;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Storefront;
using TripsAgent.IntegrationTests.Persistence;

namespace TripsAgent.IntegrationTests.Crm;

/// <summary>
/// The CRM API (#62) through the real host: the contract's routes and rules, its two permissions,
/// tenant isolation, and the storefront's anonymous endpoints — against PostgreSQL as the policed
/// application role.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CrmEndpointTests : IAsyncLifetime, IDisposable
{
    private const string Leads = "/api/v1/crm/leads";
    private const string Quotes = "/api/v1/crm/quotes";
    private const string Customers = "/api/v1/crm/customers";
    private const string Tasks = "/api/v1/crm/tasks";
    private const string Communications = "/api/v1/crm/communications";
    private const string Public = "/api/v1/public/crm";

    private static readonly string[] Everything = [PermissionCodes.CustomerView, PermissionCodes.CustomerEdit];

    private readonly PostgresFixture _postgres;

    private (string Key, string Value)[] _overrides = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _api = null!;
    private string _database = string.Empty;

    private Guid _agencyA;
    private Guid _agencyB;
    private User _adaAtA = null!;
    private User _bolaAtB = null!;

    public CrmEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>The host agency A's storefront answers on, as the placeholder directory builds it.</summary>
    private static string HostA => PlaceholderStorefrontDirectory.HostFor("lagos-travel");

    private static string HostB => PlaceholderStorefrontDirectory.HostFor("abuja-tours");

    public async Task InitializeAsync()
    {
        _database = $"crm_api_{Guid.NewGuid():N}";

        await using (var setup = await _postgres.CreateEmptyDatabaseAsync(_database))
        {
            await setup.Database.MigrateAsync();

            var a = Agency.RegisterPrincipal("Lagos Travel Limited", "lagos-travel", "NG", "NGN", "Africa/Lagos");
            var b = Agency.RegisterPrincipal("Abuja Tours Limited", "abuja-tours", "NG", "NGN", "Africa/Lagos");

            // Real user rows: a lead's owner and a task's owner are foreign keys to them, and the
            // history and the timeline show their names.
            var ada = User.ForAgency(a.Id, "ada@lagos-travel.test", "not-a-real-hash", "Ada", "Obi");
            var bola = User.ForAgency(b.Id, "bola@abuja-tours.test", "not-a-real-hash", "Bola", "Eze");

            setup.Agencies.AddRange(a, b);
            setup.Users.AddRange(ada, bola);
            await setup.SaveChangesAsync();

            (_agencyA, _agencyB) = (a.Id, b.Id);
            (_adaAtA, _bolaAtB) = (ada, bola);
        }

        // Environment variables, because a configuration source added through the factory loses to
        // appsettings.Development.json. The API runs as the role row-level security polices and
        // migrates as the owner — the same split as production (ADR-0006).
        _overrides =
        [
            ("ConnectionStrings__Postgres", _postgres.ConnectionStringFor(_database, asApplicationRole: true)),
            ("ConnectionStrings__PostgresAdmin", _postgres.ConnectionStringFor(_database, asApplicationRole: false)),
        ];

        foreach (var (key, value) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseEnvironment("Development"));

        _api = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        foreach (var (key, _) in _overrides)
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    public void Dispose()
    {
        _api?.Dispose();
        _api = null!;
    }

    // ------------------------------------------------------------------ permissions

    [Fact]
    public async Task Without_a_token_nothing_is_answered()
    {
        using var response = await _api.GetAsync(Leads);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reading_needs_customer_view()
    {
        var lead = await CreateLeadAsync();

        foreach (var path in new[] { Leads, $"{Leads}/{lead.Id}", Customers, $"{Tasks}?open=true" })
        {
            (await StatusOf(HttpMethod.Get, path, null, PermissionCodes.CustomerEdit))
                .Should().Be(HttpStatusCode.Forbidden, $"{path} reads customer records");

            (await StatusOf(HttpMethod.Get, path, null, PermissionCodes.CustomerView))
                .Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Changing_anything_needs_customer_edit()
    {
        var lead = await CreateLeadAsync();
        var quote = await CreateQuoteAsync(lead.Id);
        var task = await AddTaskAsync(lead.Id);

        var readOnly = new[] { PermissionCodes.CustomerView };

        (await StatusOf(HttpMethod.Post, Leads, NewLead(), readOnly)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Post, $"{Leads}/{lead.Id}/stage", new MoveLeadRequest("Negotiating", null), readOnly))
            .Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Post, $"{Leads}/{lead.Id}/quotes", Draft(), readOnly)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Put, $"{Quotes}/{quote.Id}", Draft(), readOnly)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Post, $"{Quotes}/{quote.Id}/send", null, readOnly)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Post, Tasks, TaskFor(lead.Id), readOnly)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Post, $"{Tasks}/{task.Id}/complete", null, readOnly)).Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(HttpMethod.Post, Communications, NoteFor(lead.Id), readOnly)).Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ tenant isolation

    [Fact]
    public async Task Another_agencys_records_do_not_exist_as_far_as_you_can_tell()
    {
        var lead = await CreateLeadAsync();
        var quote = await CreateQuoteAsync(lead.Id);
        var task = await AddTaskAsync(lead.Id);
        var customerId = lead.Customer.Id;

        (await StatusOf(HttpMethod.Get, $"{Leads}/{lead.Id}", null, _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);
        (await StatusOf(HttpMethod.Get, $"{Quotes}/{quote.Id}", null, _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);
        (await StatusOf(HttpMethod.Get, $"{Customers}/{customerId}", null, _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);

        (await StatusOf(HttpMethod.Post, $"{Leads}/{lead.Id}/stage", new MoveLeadRequest("Won", null), _agencyB, Everything))
            .Should().Be(HttpStatusCode.NotFound);
        (await StatusOf(HttpMethod.Put, $"{Quotes}/{quote.Id}", Draft(), _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);
        (await StatusOf(HttpMethod.Post, $"{Quotes}/{quote.Id}/send", null, _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);
        (await StatusOf(HttpMethod.Post, $"{Tasks}/{task.Id}/complete", null, _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);

        // And a task or a message against another agency's lead cannot be raised at all.
        (await StatusOf(HttpMethod.Post, Tasks, TaskFor(lead.Id), _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);
        (await StatusOf(HttpMethod.Post, Communications, NoteFor(lead.Id), _agencyB, Everything)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_agency_sees_only_its_own_leads_and_customers()
    {
        await CreateLeadAsync();
        await CreateLeadAsync(agencyId: _agencyB, email: "different@abuja-tours.test");

        var mine = await ListAsync<LeadSummaryResponse>(Leads, _agencyA);
        var theirs = await ListAsync<LeadSummaryResponse>(Leads, _agencyB);

        mine.Should().HaveCount(1);
        theirs.Should().HaveCount(1);
        mine[0].Id.Should().NotBe(theirs[0].Id);

        (await ListAsync<CustomerSummaryResponse>(Customers, _agencyA)).Should().HaveCount(1);
        (await ListAsync<CustomerSummaryResponse>(Customers, _agencyB)).Should().HaveCount(1);
    }

    // ------------------------------------------------------------------ customers are never keyed in first

    [Fact]
    public async Task A_lead_creates_its_customer_and_a_second_lead_finds_the_same_one()
    {
        var first = await CreateLeadAsync(email: "chiamaka@example.test");
        var second = await CreateLeadAsync(email: "chiamaka@example.test", name: "Chiamaka O.");

        second.Customer.Id.Should().Be(first.Customer.Id, "the same inbox is the same person");
        (await ListAsync<CustomerSummaryResponse>(Customers, _agencyA)).Should().HaveCount(1);

        var customer = await GetAsync<CustomerResponse>($"{Customers}/{first.Customer.Id}");
        customer.Leads.Should().HaveCount(2);
        customer.OpenLeadCount.Should().Be(2);
        customer.TotalBookings.Should().Be(0);
        customer.LifetimeValueMinor.Should().Be(0);
    }

    [Fact]
    public async Task A_lead_needs_a_name_and_a_way_to_reach_them()
    {
        var request = new LeadRequest(
            new LeadCustomerRequest(string.Empty, null, null), "Dubai", null, null, 2, 0, null, null, "Hello");

        using var response = await SendAsync(HttpMethod.Post, Leads, request, _agencyA, Everything);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorFields(await JsonAsync(response)).Should().Contain(["customer.name", "customer.email"]);
    }

    // ------------------------------------------------------------------ the pipeline

    [Fact]
    public async Task A_lead_starts_new_and_is_moved_with_its_history_written()
    {
        var lead = await CreateLeadAsync();

        lead.Stage.Should().Be("New");
        lead.Source.Should().Be("Manual");
        lead.History.Should().ContainSingle().Which.Stage.Should().Be("New");

        var moved = await PostAsync<LeadResponse>($"{Leads}/{lead.Id}/stage", new MoveLeadRequest("Negotiating", null));

        moved.Stage.Should().Be("Negotiating");
        moved.History.Should().HaveCount(2);
        moved.History[^1].ByName.Should().Be("Ada Obi");
    }

    [Fact]
    public async Task A_lost_lead_needs_a_reason()
    {
        var lead = await CreateLeadAsync();

        using (var refused = await SendAsync(HttpMethod.Post, $"{Leads}/{lead.Id}/stage", new MoveLeadRequest("Lost", "   "), _agencyA, Everything))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            ErrorFields(await JsonAsync(refused)).Should().Contain("reason");
        }

        var lost = await PostAsync<LeadResponse>($"{Leads}/{lead.Id}/stage", new MoveLeadRequest("Lost", "Booked with a cheaper agency"));

        lost.Stage.Should().Be("Lost");
        lost.LostReason.Should().Be("Booked with a cheaper agency");
        lost.History[^1].Reason.Should().Be("Booked with a cheaper agency");
    }

    [Fact]
    public async Task Moving_a_lead_to_where_it_already_is_is_a_409()
    {
        var lead = await CreateLeadAsync();

        (await StatusOf(HttpMethod.Post, $"{Leads}/{lead.Id}/stage", new MoveLeadRequest("New", null), Everything))
            .Should().Be(HttpStatusCode.Conflict);
    }

    // ------------------------------------------------------------------ quotes

    [Fact]
    public async Task A_quote_is_numbered_per_agency_and_totalled_from_its_items()
    {
        var lead = await CreateLeadAsync();

        var first = await CreateQuoteAsync(lead.Id);
        var second = await CreateQuoteAsync(lead.Id);

        first.QuoteNumber.Should().Be("QT-0001");
        second.QuoteNumber.Should().Be("QT-0002");

        // Two nights at ₦450,000 and one transfer at ₦75,000.
        first.TotalMinor.Should().Be((2 * 45_000_000L) + 7_500_000L);
        first.Status.Should().Be("Draft");
        first.PublicUrl.Should().BeNull();

        var theirs = await CreateQuoteAsync((await CreateLeadAsync(agencyId: _agencyB, email: "theirs@abuja-tours.test")).Id, _agencyB);
        theirs.QuoteNumber.Should().Be("QT-0001", "each agency numbers its own quotes");
    }

    [Fact]
    public async Task Sending_a_quote_gives_it_a_link_on_the_agencys_own_site_and_moves_a_new_lead_to_quoted()
    {
        var lead = await CreateLeadAsync();
        var quote = await CreateQuoteAsync(lead.Id);

        var sent = await PostAsync<QuoteResponse>($"{Quotes}/{quote.Id}/send", null);

        sent.Status.Should().Be("Sent");
        sent.SentAt.Should().NotBeNull();
        sent.PublicUrl.Should().StartWith($"https://{HostA}/q/");
        sent.PublicUrl.Should().NotContain("trips", "nothing traveller-facing may mention us");

        (await GetAsync<LeadResponse>($"{Leads}/{lead.Id}")).Stage.Should().Be("Quoted");
    }

    [Fact]
    public async Task Sending_a_quote_queues_the_customers_email_in_the_agencys_branding()
    {
        var lead = await CreateLeadAsync(email: "chiamaka@example.test");
        var quote = await CreateQuoteAsync(lead.Id);

        var sent = await PostAsync<QuoteResponse>($"{Quotes}/{quote.Id}/send", null);

        var (tenant, scope) = TestTenancy.For(_agencyA);
        await using var db = _postgres.Connect(_database, tenant, scope);

        var notification = await db.Notifications.SingleAsync();

        notification.TemplateKey.Should().Be("crm.quote-sent");
        notification.RecipientAddress.Should().Be("chiamaka@example.test");
        notification.Payload.Should().Contain(sent.PublicUrl);
        notification.Payload.Should().Contain(sent.QuoteNumber);
    }

    [Fact]
    public async Task A_lead_already_negotiating_stays_there_when_another_quote_is_sent()
    {
        var lead = await CreateLeadAsync();
        await PostAsync<LeadResponse>($"{Leads}/{lead.Id}/stage", new MoveLeadRequest("Negotiating", null));

        var quote = await CreateQuoteAsync(lead.Id);
        await PostAsync<QuoteResponse>($"{Quotes}/{quote.Id}/send", null);

        (await GetAsync<LeadResponse>($"{Leads}/{lead.Id}")).Stage.Should().Be("Negotiating");
    }

    [Fact]
    public async Task A_sent_quote_is_never_edited()
    {
        var lead = await CreateLeadAsync();
        var quote = await CreateQuoteAsync(lead.Id);

        (await StatusOf(HttpMethod.Put, $"{Quotes}/{quote.Id}", Draft("Revised"), Everything)).Should().Be(HttpStatusCode.OK);

        await PostAsync<QuoteResponse>($"{Quotes}/{quote.Id}/send", null);

        (await StatusOf(HttpMethod.Put, $"{Quotes}/{quote.Id}", Draft("Revised again"), Everything))
            .Should().Be(HttpStatusCode.Conflict);
        (await StatusOf(HttpMethod.Post, $"{Quotes}/{quote.Id}/send", null, Everything))
            .Should().Be(HttpStatusCode.Conflict, "it has already been sent");
    }

    [Fact]
    public async Task A_quote_with_nothing_on_it_cannot_be_sent()
    {
        var lead = await CreateLeadAsync();
        var empty = new QuoteRequest("Ideas so far", Today.AddDays(14), [], [], string.Empty);

        using var response = await SendAsync(HttpMethod.Post, $"{Leads}/{lead.Id}/quotes", empty, _agencyA, Everything);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        ErrorFields(await JsonAsync(response)).Should().Contain("items");
    }

    // ------------------------------------------------------------------ tasks and the timeline

    [Fact]
    public async Task A_task_against_a_lead_is_labelled_and_shows_on_its_customers_record()
    {
        var lead = await CreateLeadAsync();
        var task = await AddTaskAsync(lead.Id);

        task.Related.Type.Should().Be("Lead");
        task.Related.Label.Should().Be("Chiamaka Okonkwo · Dubai");
        task.OwnerName.Should().Be("Ada Obi");
        task.CompletedAt.Should().BeNull();

        (await GetAsync<CustomerResponse>($"{Customers}/{lead.Customer.Id}")).Tasks.Should().ContainSingle();
        (await GetAsync<LeadResponse>($"{Leads}/{lead.Id}")).NextTaskDueAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Completing_a_completed_task_is_a_409()
    {
        var lead = await CreateLeadAsync();
        var task = await AddTaskAsync(lead.Id);

        (await PostAsync<TaskResponse>($"{Tasks}/{task.Id}/complete", null)).CompletedAt.Should().NotBeNull();

        (await StatusOf(HttpMethod.Post, $"{Tasks}/{task.Id}/complete", null, Everything)).Should().Be(HttpStatusCode.Conflict);

        (await ListAsync<TaskResponse>($"{Tasks}?open=true", _agencyA)).Should().BeEmpty();
        (await ListAsync<TaskResponse>(Tasks, _agencyA)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_message_is_logged_against_the_lead_and_reaches_the_customers_timeline()
    {
        var lead = await CreateLeadAsync();

        var outbound = await PostAsync<CommunicationResponse>(Communications, NoteFor(lead.Id));

        outbound.ByName.Should().Be("Ada Obi");
        outbound.Channel.Should().Be("Whatsapp");
        outbound.Direction.Should().Be("Outbound");

        var inbound = await PostAsync<CommunicationResponse>(
            Communications,
            new CommunicationRequest("Call", "Inbound", "She called back about dates.", new RelatedRecord("Lead", lead.Id)));

        inbound.ByName.Should().Be("Chiamaka Okonkwo", "an inbound message came from the customer");

        (await GetAsync<CustomerResponse>($"{Customers}/{lead.Customer.Id}")).Communications.Should().HaveCount(2);
        (await GetAsync<LeadResponse>($"{Leads}/{lead.Id}")).Communications.Should().HaveCount(2);
    }

    // ------------------------------------------------------------------ the storefront

    [Fact]
    public async Task The_trip_request_widget_opens_a_new_lead_for_the_agency_whose_host_was_used()
    {
        using var response = await PostPublicAsync($"{Public}/trip-requests", HostA, Submission());

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var leads = await ListAsync<LeadSummaryResponse>(Leads, _agencyA);

        leads.Should().ContainSingle();
        leads[0].Source.Should().Be("TripRequestWidget");
        leads[0].Stage.Should().Be("New");
        leads[0].OwnerName.Should().BeNull("nobody has picked it up yet");
        leads[0].Customer.Email.Should().Be("ifeoma@example.test");

        (await ListAsync<LeadSummaryResponse>(Leads, _agencyB)).Should().BeEmpty();

        var full = await GetAsync<LeadResponse>($"{Leads}/{leads[0].Id}");
        full.History.Should().ContainSingle().Which.ByName.Should().Be("Website");
    }

    [Fact]
    public async Task A_second_trip_request_from_the_same_person_updates_the_one_customer()
    {
        (await PostPublicAsync($"{Public}/trip-requests", HostA, Submission())).Dispose();
        (await PostPublicAsync($"{Public}/trip-requests", HostA, Submission("Accra"))).Dispose();

        (await ListAsync<LeadSummaryResponse>(Leads, _agencyA)).Should().HaveCount(2);
        (await ListAsync<CustomerSummaryResponse>(Customers, _agencyA)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_host_no_storefront_answers_on_is_a_404()
    {
        using var unknown = await PostPublicAsync($"{Public}/trip-requests", "somebody-elses-site.test", Submission());

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await ListAsync<LeadSummaryResponse>(Leads, _agencyA)).Should().BeEmpty();
        (await ListAsync<LeadSummaryResponse>(Leads, _agencyB)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_quote_link_asked_for_on_the_wrong_agencys_host_is_not_found()
    {
        var token = await SendQuoteAsync();

        using var wrongHost = await GetPublicAsync($"{Public}/quotes/{token}", HostB);
        using var noHost = await GetPublicAsync($"{Public}/quotes/{token}", "nobody.test");
        using var rightHost = await GetPublicAsync($"{Public}/quotes/{token}", HostA);

        wrongHost.StatusCode.Should().Be(HttpStatusCode.NotFound, "the link is not agency B's to serve");
        noHost.StatusCode.Should().Be(HttpStatusCode.NotFound);
        rightHost.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_that_is_not_a_token_is_not_found()
    {
        await SendQuoteAsync();

        foreach (var token in new[] { "short", new string('a', 43), new string('!', 43) })
        {
            using var response = await GetPublicAsync($"{Public}/quotes/{token}", HostA);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Viewing_a_quote_records_the_first_view_only()
    {
        var token = await SendQuoteAsync();

        var first = await ReadPublicAsync($"{Public}/quotes/{token}", HostA);
        first.Status.Should().Be("Viewed");
        first.CanRespond.Should().BeTrue();
        first.CustomerName.Should().Be("Chiamaka Okonkwo");
        first.Items.Should().HaveCount(2);

        var seenAt = (await GetAsync<QuoteResponse>($"{Quotes}/{await QuoteIdAsync()}")).ViewedAt;
        seenAt.Should().NotBeNull();

        await ReadPublicAsync($"{Public}/quotes/{token}", HostA);

        (await GetAsync<QuoteResponse>($"{Quotes}/{await QuoteIdAsync()}")).ViewedAt.Should().Be(seenAt);
    }

    [Fact]
    public async Task A_customer_accepts_their_quote_once_and_the_lead_starts_negotiating()
    {
        var token = await SendQuoteAsync();

        var accepted = await PostPublicAsync($"{Public}/quotes/{token}/accept", HostA, null);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        accepted.Dispose();

        var quote = await GetAsync<QuoteResponse>($"{Quotes}/{await QuoteIdAsync()}");
        quote.Status.Should().Be("Accepted");
        quote.RespondedAt.Should().NotBeNull();

        var lead = (await ListAsync<LeadSummaryResponse>(Leads, _agencyA))[0];
        var full = await GetAsync<LeadResponse>($"{Leads}/{lead.Id}");

        full.Stage.Should().Be("Negotiating", "Won is the agent's call, not the website's");
        full.Communications.Should().ContainSingle()
            .Which.Summary.Should().Contain("Accepted QT-0001");

        using var again = await PostPublicAsync($"{Public}/quotes/{token}/decline", HostA, new DeclineQuoteRequest("Changed my mind"));
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_customer_declines_with_a_reason_and_it_reaches_the_timeline()
    {
        var token = await SendQuoteAsync();

        using (var declined = await PostPublicAsync(
            $"{Public}/quotes/{token}/decline", HostA, new DeclineQuoteRequest("Over our budget")))
        {
            declined.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var lead = (await ListAsync<LeadSummaryResponse>(Leads, _agencyA))[0];

        (await GetAsync<LeadResponse>($"{Leads}/{lead.Id}")).Communications
            .Should().ContainSingle()
            .Which.Summary.Should().Contain("Over our budget");

        (await GetAsync<QuoteResponse>($"{Quotes}/{await QuoteIdAsync()}")).Status.Should().Be("Declined");
    }

    [Fact]
    public async Task The_storefront_routes_need_no_token_at_all()
    {
        // They are anonymous by design: a traveller on an agency's own site is signed in to nothing.
        using var response = await PostPublicAsync($"{Public}/trip-requests", HostA, Submission());

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    // ------------------------------------------------------------------ helpers

    private static LeadRequest NewLead(string email = "chiamaka@example.test", string name = "Chiamaka Okonkwo") =>
        new(
            new LeadCustomerRequest(name, email, "+234 803 000 1122"),
            "Dubai",
            Today.AddMonths(3),
            Today.AddMonths(3).AddDays(7),
            2,
            1,
            300_000_00L,
            500_000_00L,
            "Somewhere warm in March, please.");

    private static QuoteRequest Draft(string title = "Dubai, seven nights") =>
        new(
            title,
            Today.AddDays(14),
            [
                new QuoteItemRequest("Hotel, two nights", 2, 45_000_000L, null),
                new QuoteItemRequest("Airport transfer", 1, 7_500_000L, null),
            ],
            [
                new QuoteDayRequest(1, "Arrive", "Transfer to the hotel."),
                new QuoteDayRequest(2, "Desert", "Dune drive and dinner."),
            ],
            "Prices hold until the date above.");

    private static TaskRequest TaskFor(Guid leadId) =>
        new("Call her back about dates", DateTimeOffset.UtcNow.AddDays(1), new RelatedRecord("Lead", leadId));

    private static CommunicationRequest NoteFor(Guid leadId) =>
        new("Whatsapp", "Outbound", "Sent her three hotel options.", new RelatedRecord("Lead", leadId));

    private static TripRequestSubmission Submission(string destination = "Dubai") =>
        new(
            "Ifeoma Nwosu",
            "ifeoma@example.test",
            "+234 802 555 0000",
            destination,
            Today.AddMonths(2),
            Today.AddMonths(2).AddDays(5),
            2,
            0,
            null,
            400_000_00L,
            "We would like a quiet beach.");

    private async Task<LeadResponse> CreateLeadAsync(
        Guid? agencyId = null,
        string email = "chiamaka@example.test",
        string name = "Chiamaka Okonkwo")
    {
        using var response = await SendAsync(HttpMethod.Post, Leads, NewLead(email, name), agencyId ?? _agencyA, Everything);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<LeadResponse>())!;
    }

    private async Task<QuoteResponse> CreateQuoteAsync(Guid leadId, Guid? agencyId = null)
    {
        using var response = await SendAsync(
            HttpMethod.Post, $"{Leads}/{leadId}/quotes", Draft(), agencyId ?? _agencyA, Everything);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<QuoteResponse>())!;
    }

    private Task<TaskResponse> AddTaskAsync(Guid leadId) => PostAsync<TaskResponse>(Tasks, TaskFor(leadId));

    /// <summary>A lead, a quote and a send, and the token out of the link the customer was given.</summary>
    private async Task<string> SendQuoteAsync()
    {
        var lead = await CreateLeadAsync();
        var quote = await CreateQuoteAsync(lead.Id);
        var sent = await PostAsync<QuoteResponse>($"{Quotes}/{quote.Id}/send", null);

        var link = sent.PublicUrl!;

        return link[(link.LastIndexOf('/') + 1)..];
    }

    private async Task<Guid> QuoteIdAsync() =>
        (await GetAsync<LeadResponse>($"{Leads}/{(await ListAsync<LeadSummaryResponse>(Leads, _agencyA))[0].Id}")).Quotes[0].Id;

    private async Task<T> GetAsync<T>(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, _agencyA, Everything);

        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<List<T>> ListAsync<T>(string path, Guid agencyId)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, agencyId, Everything);

        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return (await response.Content.ReadFromJsonAsync<List<T>>())!;
    }

    private async Task<T> PostAsync<T>(string path, object? body)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, _agencyA, Everything);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private Task<HttpStatusCode> StatusOf(HttpMethod method, string path, object? body, params string[] permissions) =>
        StatusOf(method, path, body, _agencyA, permissions);

    private async Task<HttpStatusCode> StatusOf(HttpMethod method, string path, object? body, Guid agencyId, params string[] permissions)
    {
        using var response = await SendAsync(method, path, body, agencyId, permissions);
        return response.StatusCode;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        Guid agencyId,
        params string[] permissions)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(agencyId, permissions));

        return await _api.SendAsync(request);
    }

    /// <summary>An anonymous GET, as a traveller's browser on <paramref name="host"/> makes it.</summary>
    private async Task<HttpResponseMessage> GetPublicAsync(string path, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(PublicCrmEndpoints.StorefrontHostHeader, host);

        return await _api.SendAsync(request);
    }

    private async Task<PublicQuoteResponse> ReadPublicAsync(string path, string host)
    {
        using var response = await GetPublicAsync(path, host);

        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return (await response.Content.ReadFromJsonAsync<PublicQuoteResponse>())!;
    }

    private async Task<HttpResponseMessage> PostPublicAsync(string path, string host, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(PublicCrmEndpoints.StorefrontHostHeader, host);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        return await _api.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static List<string> ErrorFields(JsonElement body) =>
        body.GetProperty("errors").EnumerateObject().Select(property => property.Name).ToList();

    /// <summary>A real token from the API's own issuer, carrying exactly <paramref name="permissions"/>.</summary>
    private string TokenFor(Guid agencyId, IReadOnlyCollection<string> permissions)
    {
        var issuer = _factory.Services.GetRequiredService<IAccessTokenIssuer>();
        var user = agencyId == _agencyA ? _adaAtA : _bolaAtB;

        return issuer.Issue(user, ["Manager"], permissions, agencyId).Value;
    }
}
