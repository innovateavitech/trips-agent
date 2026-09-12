using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Assets;
using TripsAgent.Application.Catalog;
using TripsAgent.Application.Crm;
using TripsAgent.Application.Documents;
using TripsAgent.Application.Identity.Authentication;
using TripsAgent.Application.Identity.Registration;
using TripsAgent.Application.Notifications;
using TripsAgent.Application.Orders;
using TripsAgent.Application.Payments;
using TripsAgent.Application.Pricing;
using TripsAgent.Application.Search;
using TripsAgent.Application.Storefront;
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

        // Records what was sold, at the price the quote froze. The saga (#42) takes it from there.
        services.AddScoped<PlaceOrderHandler>();
        services.AddScoped<ConfigureDocumentNumberingHandler>();

        services.AddScoped<PricingService>();
        services.AddScoped<MarkupRuleService>();

        // Zero until subscription tiers (#64) supply each agency's transaction fee.
        services.AddSingleton<IPlatformFeePolicy, NoPlatformFeePolicy>();

        // Picks the adapter for a supplier and product from whatever adapters the host registered.
        // Adding an aggregator is a new ISupplierAdapter registration, never a change here.
        services.AddScoped<ISupplierAdapterRegistry, SupplierAdapterRegistry>();

        // Flight and bus search: cached at the net rate, priced with the agency's markup on every read (#40).
        services.AddScoped<SupplierSearchService>();

        // Confirms a booking's price and raises a P1 alert when a supplier hash fails (#35).
        services.AddScoped<PriceConfirmationService>();

        // The booking pipeline after confirmation: issue once and never again (#36), learn the outcome
        // by polling because there are no webhooks (#37), and lapse held bookings at their ticket time
        // limit (#38). The Worker runs all three; the API only ever asks for the first.
        services.AddScoped<TicketIssuanceService>();
        services.AddScoped<SupplierBookingStatusPoller>();
        services.AddScoped<TicketTimeLimitMonitor>();

        // The checkout (#42): confirm and pay, capture on the ticket, and sweep up lost issue messages.
        // Reversals (#43) and the agent's resolution queue (#44) give money back through one path.
        services.AddScoped<LedgerAccounts>();
        services.AddScoped<Checkout.CheckoutService>();
        services.AddScoped<Checkout.CheckoutCompletion>();
        services.AddScoped<Checkout.CheckoutSweeper>();
        services.AddScoped<Checkout.WalletRefunds>();
        services.AddScoped<Checkout.PaymentReversalService>();
        services.AddScoped<Checkout.ResolutionService>();
        services.AddScoped<Checkout.BookingQueries>();

        // The request-side half of an upload. The pipeline itself — ProcessAssetHandler — is
        // registered by AddAssetProcessing in the Worker only, because it needs a virus scanner
        // and an image library that the API has no business loading.
        services.AddScoped<RequestAssetUploadHandler>();
        services.AddScoped<CompleteAssetUploadHandler>();
        services.AddScoped<GetAssetHandler>();
        services.AddScoped<AssetDelivery>();

        // The agent-authored catalog: tours, packages and visas, and their categories (#160, #161).
        services.AddScoped<ProductCatalogService>();
        services.AddScoped<ProductCategoryService>();

        // The website builder: editing the draft, and staging, publishing and rolling back versions.
        services.AddScoped<SiteQueries>();
        services.AddScoped<SiteBuilderService>();
        services.AddScoped<SiteVersionService>();
        services.AddScoped<SitePreviewTokens>();

        // The website's addresses: connecting and checking the agency's own domains, the two sweeps the
        // Worker runs on a clock, and the platform's review queue for brand-like addresses.
        services.AddScoped<DomainVerifier>();
        services.AddScoped<SiteDomainService>();
        services.AddScoped<DomainVerificationSweep>();
        services.AddScoped<CertificateSweep>();
        services.AddScoped<HostnameReviewService>();

        // The traveller-facing side: resolving the hostname to an agency, and reading that agency's
        // published site and catalog. Anonymous, and read-only.
        services.AddScoped<PublicSiteResolver>();
        services.AddScoped<PublicSiteService>();
        services.AddScoped<PublicCatalogService>();

        // The CRM: leads, quotes, customers, tasks and the timeline (#62), and the storefront's own
        // anonymous side of it. CrmContext and CrmReader are shared by all of them.
        services.AddScoped<CrmContext>();
        services.AddScoped<CrmReader>();
        services.AddScoped<CustomerDirectory>();
        services.AddScoped<LeadService>();
        services.AddScoped<QuoteService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<FollowUpService>();
        services.AddScoped<StorefrontCrmService>();
        services.AddScoped<CustomerBookingRecorder>();
        services.AddScoped<TaskReminders>();
        services.AddScoped<IQuoteEmails, QuoteEmails>();

        // Stages notifications in the caller's unit of work; the Worker sends them.
        services.AddScoped<INotifier, Notifier>();

        // What the email provider says happened after it accepted a message — delivered, bounced,
        // reported as spam (#45). Its webhook arrives with whichever provider is chosen.
        services.AddScoped<NotificationDeliveryReports>();

        // The traveller's side of the booking pipeline (#42–#46): its emails and the order's documents,
        // staged by BookingFollowUps when the Worker consumes the pipeline's BookingConfirmed,
        // BookingNeedsResolution and PaymentReversed events.
        services.AddScoped<IBookingEmails, BookingEmails>();
        services.AddScoped<IBookingDocuments, BookingDocuments>();
        services.AddScoped<BookingFollowUps>();

        // Invoices and vouchers in the console (#46): list, reissue and download through signed
        // links. The rendering itself — OrderDocumentService — is registered by AddDocumentRendering,
        // in the Worker only, because it needs the PDF renderer the API has no business loading.
        services.AddScoped<BookingDocumentsHandler>();
        services.AddScoped<DocumentLinks>();

        return services;
    }
}
