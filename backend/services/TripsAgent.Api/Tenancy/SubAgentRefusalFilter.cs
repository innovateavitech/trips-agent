using TripsAgent.Application.Tenancy.SubAgents;

namespace TripsAgent.Api.Tenancy;

/// <summary>
/// Turns a <see cref="SubAgentRefusedException"/> into the right HTTP problem.
/// </summary>
/// <remarks>
/// <para>
/// A filter rather than a try/catch in each route: there are two dozen routes in this feature and
/// they would all have written the same five-line catch. The refusal reason decides the status
/// code in one place, so "not one of yours" cannot be a 404 on one route and a 403 on the next.
/// </para>
/// <para>
/// Only this one exception is caught. Everything else goes to the host's handler, which is right:
/// an unexpected failure must not be dressed up as a tidy 400.
/// </para>
/// </remarks>
public sealed class SubAgentRefusalFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            return await next(context);
        }
        catch (SubAgentRefusedException refused)
        {
            return refused.Refusal switch
            {
                SubAgentRefusal.Invalid => Results.ValidationProblem(
                    new Dictionary<string, string[]>(StringComparer.Ordinal)
                    {
                        ["request"] = [Detailed(refused)],
                    },
                    title: refused.Message),

                SubAgentRefusal.Forbidden => Problem(refused, StatusCodes.Status403Forbidden),
                SubAgentRefusal.NotFound => Problem(refused, StatusCodes.Status404NotFound),
                _ => Problem(refused, StatusCodes.Status409Conflict),
            };
        }
    }

    private static IResult Problem(SubAgentRefusedException refused, int statusCode) =>
        Results.Problem(statusCode: statusCode, title: refused.Message, detail: refused.Detail);

    private static string Detailed(SubAgentRefusedException refused) =>
        refused.Detail is null ? refused.Message : $"{refused.Message} {refused.Detail}";
}
