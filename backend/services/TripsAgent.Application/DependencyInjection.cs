using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Identity.Registration;

namespace TripsAgent.Application;

/// <summary>Registers the use cases. Called once from the API's startup.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped, because every one of these works through the request's DbContext and tenant.
        services.AddScoped<VerificationCodeIssuer>();
        services.AddScoped<RegisterAgentHandler>();
        services.AddScoped<VerifyEmailHandler>();
        services.AddScoped<ResendVerificationHandler>();

        return services;
    }
}
