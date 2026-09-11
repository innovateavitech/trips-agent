using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Assets;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Identity.Authentication;
using TripsAgent.Application.Identity.Registration;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Pricing;
using TripsAgent.Application.Suppliers;
using TripsAgent.Application.Tenancy.Kyb;

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
        services.AddScoped<ForgotPasswordHandler>();
        services.AddScoped<ResetPasswordHandler>();

        services.AddScoped<TokenPairFactory>();
        services.AddScoped<LoginHandler>();
        services.AddScoped<RefreshTokenHandler>();
        services.AddScoped<LogoutHandler>();

        services.AddScoped<UploadKybDocumentHandler>();
        services.AddScoped<SubmitKybHandler>();
        services.AddScoped<GetKybStatusHandler>();
        services.AddScoped<KybReviewHandler>();
        services.AddScoped<KybDocumentLink>();

        services.AddScoped<WalletTopUpService>();
        services.AddScoped<StartTopUpHandler>();
        services.AddScoped<VerifyTopUpHandler>();
        services.AddScoped<PaymentWebhookHandler>();

        // Hangfire resolves the processor by interface when a job runs, and the webhook handler
        // is the implementation — one class, so the receive and process halves cannot drift.
        services.AddScoped<IPaymentWebhookProcessor>(sp => sp.GetRequiredService<PaymentWebhookHandler>());

        services.AddScoped<ILedgerIntegrityAudit, LedgerIntegrityAudit>();

        services.AddScoped<DocumentIssuer>();
        services.AddScoped<ConfigureDocumentNumberingHandler>();

        services.AddScoped<PricingService>();
        services.AddScoped<MarkupRuleService>();

        // Zero until subscription tiers (#64) supply each agency's transaction fee.
        services.AddSingleton<IPlatformFeePolicy, NoPlatformFeePolicy>();

        // Picks the adapter for a supplier and product from whatever adapters the host registered.
        // Adding an aggregator is a new ISupplierAdapter registration, never a change here.
        services.AddScoped<ISupplierAdapterRegistry, SupplierAdapterRegistry>();

        // The request-side half of an upload. The pipeline itself — ProcessAssetHandler — is
        // registered by AddAssetProcessing in the Worker only, because it needs a virus scanner
        // and an image library that the API has no business loading.
        services.AddScoped<RequestAssetUploadHandler>();
        services.AddScoped<CompleteAssetUploadHandler>();
        services.AddScoped<GetAssetHandler>();
        services.AddScoped<AssetDelivery>();

        return services;
    }
}
