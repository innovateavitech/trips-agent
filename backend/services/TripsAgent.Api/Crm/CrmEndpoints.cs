using TripsAgent.Api.Authorization;
using TripsAgent.Application.Crm;
using TripsAgent.Contracts.Crm;
using TripsAgent.Domain.Crm;
using TripsAgent.Domain.Identity;

namespace TripsAgent.Api.Crm;

/// <summary>
/// The console's CRM API (#62): the pipeline, quotes, customers, follow-up tasks and the
/// communication timeline.
/// </summary>
/// <remarks>
/// <para>
/// The routes and field names are the CRM contract in <c>docs/BUILD_PLAN.md</c> (F7), which the
/// console's screens are built to. <c>customer.view</c> reads and <c>customer.edit</c> changes
/// anything — a counter agent can be given the inbox to read without being able to move a lead or
/// send a quote.
/// </para>
/// <para>
/// Statuses, once, for every route here: 404 for a record this agency does not have — which is what
/// another agency's id looks like — 422 with every problem keyed by its field, and 409 when the
/// request was well-formed but the record is not in a state to take it: a lead already at that stage,
/// a sent quote being edited, a task already done.
/// </para>
/// </remarks>
public static class CrmEndpoints
{
    public static IEndpointRouteBuilder MapCrmEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/crm")
            .WithTags("CRM")

            // Every route is the caller's own agency's. Without a token there is no tenant and the
            // filters would return nothing: an honest 401 beats a baffling empty list.
            .RequireAuthorization();

        MapLeads(group);
        MapQuotes(group);
        MapCustomers(group);
        MapFollowUps(group);

        return app;
    }

    private static void MapLeads(RouteGroupBuilder group)
    {
        group.MapGet("/leads", async (LeadService leads, CancellationToken cancellationToken) =>
                Results.Ok(await leads.ListAsync(cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerView))
            .WithName("ListLeads")
            .Produces<List<LeadSummaryResponse>>();

        group.MapGet("/leads/{leadId:guid}", async (
                Guid leadId,
                LeadService leads,
                CancellationToken cancellationToken) =>
            {
                var lead = await leads.GetAsync(leadId, cancellationToken);

                return lead is null ? NoSuchLead() : Results.Ok(lead);
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerView))
            .WithName("GetLead")
            .Produces<LeadResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // The lead an agent keys in. The storefront's widget opens its own, anonymously — see
        // PublicCrmEndpoints — and neither creates a customer that was keyed in first.
        group.MapPost("/leads", async (
                LeadRequest request,
                LeadService leads,
                CancellationToken cancellationToken) =>
            {
                var outcome = await leads.CreateAsync(request, cancellationToken);

                return ToResult(outcome, created: lead => $"/api/v1/crm/leads/{lead.Id}");
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerEdit))
            .WithName("CreateLead")
            .Produces<LeadResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/leads/{leadId:guid}/stage", async (
                Guid leadId,
                MoveLeadRequest request,
                LeadService leads,
                CancellationToken cancellationToken) =>
                ToResult(await leads.MoveAsync(leadId, request, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerEdit))
            .WithName("MoveLead")
            .Produces<LeadResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapQuotes(RouteGroupBuilder group)
    {
        group.MapPost("/leads/{leadId:guid}/quotes", async (
                Guid leadId,
                QuoteRequest request,
                QuoteService quotes,
                CancellationToken cancellationToken) =>
                ToResult(
                    await quotes.CreateAsync(leadId, request, cancellationToken),
                    created: quote => $"/api/v1/crm/quotes/{quote.Id}"))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerEdit))
            .WithName("CreateQuote")
            .Produces<QuoteResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/quotes/{quoteId:guid}", async (
                Guid quoteId,
                QuoteService quotes,
                CancellationToken cancellationToken) =>
            {
                var quote = await quotes.GetAsync(quoteId, cancellationToken);

                return quote is null ? NoSuchQuote() : Results.Ok(quote);
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerView))
            .WithName("GetQuote")
            .Produces<QuoteResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Saves the whole draft: the items and days in the request replace the ones on it. A sent
        // quote is refused — the customer has it as it was sent.
        group.MapPut("/quotes/{quoteId:guid}", async (
                Guid quoteId,
                QuoteRequest request,
                QuoteService quotes,
                CancellationToken cancellationToken) =>
                ToResult(await quotes.SaveAsync(quoteId, request, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerEdit))
            .WithName("SaveQuote")
            .Produces<QuoteResponse>()
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/quotes/{quoteId:guid}/send", async (
                Guid quoteId,
                QuoteService quotes,
                CancellationToken cancellationToken) =>
                ToResult(await quotes.SendAsync(quoteId, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerEdit))
            .WithName("SendQuote")
            .Produces<QuoteResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapCustomers(RouteGroupBuilder group)
    {
        group.MapGet("/customers", async (CustomerService customers, CancellationToken cancellationToken) =>
                Results.Ok(await customers.ListAsync(cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerView))
            .WithName("ListCustomers")
            .Produces<List<CustomerSummaryResponse>>();

        group.MapGet("/customers/{customerId:guid}", async (
                Guid customerId,
                CustomerService customers,
                CancellationToken cancellationToken) =>
            {
                var customer = await customers.GetAsync(customerId, cancellationToken);

                return customer is null
                    ? Results.Problem(
                        statusCode: StatusCodes.Status404NotFound,
                        title: "We could not find that customer.")
                    : Results.Ok(customer);
            })
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerView))
            .WithName("GetCustomer")
            .Produces<CustomerResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapFollowUps(RouteGroupBuilder group)
    {
        group.MapGet("/tasks", async (
                bool? open,
                FollowUpService followUps,
                CancellationToken cancellationToken) =>
                Results.Ok(await followUps.ListTasksAsync(open ?? false, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerView))
            .WithName("ListTasks")
            .Produces<List<TaskResponse>>();

        group.MapPost("/tasks", async (
                TaskRequest request,
                FollowUpService followUps,
                CancellationToken cancellationToken) =>
                ToResult(await followUps.AddTaskAsync(request, cancellationToken), created: _ => null))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerEdit))
            .WithName("AddTask")
            .Produces<TaskResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/tasks/{taskId:guid}/complete", async (
                Guid taskId,
                FollowUpService followUps,
                CancellationToken cancellationToken) =>
                ToResult(await followUps.CompleteTaskAsync(taskId, cancellationToken)))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerEdit))
            .WithName("CompleteTask")
            .Produces<TaskResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // A note of what was said elsewhere. Nothing is sent from here: SMS and WhatsApp are logged,
        // not sent.
        group.MapPost("/communications", async (
                CommunicationRequest request,
                FollowUpService followUps,
                CancellationToken cancellationToken) =>
                ToResult(await followUps.LogAsync(request, cancellationToken), created: _ => null))
            .RequireAuthorization(PermissionPolicies.For(PermissionCodes.CustomerEdit))
            .WithName("LogCommunication")
            .Produces<CommunicationResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// The one place a <see cref="CrmResult{T}"/> becomes a status, so every CRM route answers the
    /// same shape for the same kind of outcome.
    /// </summary>
    /// <param name="outcome">What the service made of the request.</param>
    /// <param name="created">
    /// Where the new record lives, for a route that creates one — null for a 200. A null return from
    /// it means 201 with no Location header, for a record with no route of its own.
    /// </param>
    internal static IResult ToResult<T>(CrmResult<T> outcome, Func<T, string?>? created = null) => outcome switch
    {
        CrmResult<T>.Done done when created is not null =>
            Results.Created(created(done.Value), done.Value),

        CrmResult<T>.Done done => Results.Ok(done.Value),

        CrmResult<T>.NotFound missing => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: missing.Title),

        // 422, with every problem keyed by the field it belongs to, so the console can put each
        // message beside its input in one pass.
        CrmResult<T>.Invalid invalid => Results.ValidationProblem(
            ByField(invalid.Problems),
            title: invalid.Title,
            statusCode: StatusCodes.Status422UnprocessableEntity),

        CrmResult<T>.Refused refused => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: refused.Title,
            detail: refused.Detail),

        _ => throw new InvalidOperationException($"Unhandled CRM outcome {outcome?.GetType().Name}."),
    };

    /// <summary>The problems grouped by field, in the shape a validation problem response wants.</summary>
    internal static Dictionary<string, string[]> ByField(IReadOnlyList<CrmProblem> problems) =>
        problems
            .GroupBy(problem => problem.Field, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(problem => problem.Message).ToArray(),
                StringComparer.Ordinal);

    private static IResult NoSuchLead() => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "We could not find that lead.",
        detail: "It may belong to another agency.");

    private static IResult NoSuchQuote() => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "We could not find that quote.");
}
