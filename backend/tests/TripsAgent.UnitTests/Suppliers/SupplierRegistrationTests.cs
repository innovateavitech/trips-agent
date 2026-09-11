using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TripsAgent.Application.Security;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;
using TripsAgent.Infrastructure;
using TripsAgent.Infrastructure.Security;
using TripsAgent.Infrastructure.Suppliers;

namespace TripsAgent.UnitTests.Suppliers;

/// <summary>
/// How supplier credentials and the call log are wired: the key setting, the open-question-1 switch,
/// the redaction of decrypted credentials, and the maintenance schedule.
/// </summary>
public class SupplierRegistrationTests
{
    private const string UnusedConnectionString =
        "Host=never.connected.invalid;Database=none;Username=none;Password=none";

    // ------------------------------------------------------------------ the encryption key

    [Fact]
    public void A_missing_encryption_key_fails_clearly_when_a_secret_is_first_needed()
    {
        using var provider = Build(new Dictionary<string, string?>());

        var resolve = () => provider.GetRequiredService<ISecretProtector>();

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*Security__SecretEncryptionKey*")
            .And.Message.Should().Contain("openssl rand -base64 32");
    }

    [Fact]
    public void A_configured_key_gives_an_AES_GCM_protector()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            [DependencyInjection.SecretEncryptionKeySetting] = Convert.ToBase64String(new byte[AesGcmSecretProtector.KeyBytes]),
        });

        provider.GetRequiredService<ISecretProtector>().Should().BeOfType<AesGcmSecretProtector>();
    }

    [Fact]
    public void A_key_that_is_not_base64_is_reported_as_such()
    {
        using var provider = Build(new Dictionary<string, string?> { [DependencyInjection.SecretEncryptionKeySetting] = "not base64!" });

        var resolve = () => provider.GetRequiredService<ISecretProtector>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*not valid base64*");
    }

    // ------------------------------------------------------------------ open question 1

    [Fact]
    public void Calls_use_the_platform_credential_unless_configured_otherwise()
    {
        using var provider = Build(new Dictionary<string, string?>());

        provider.GetRequiredService<SupplierCredentialOptions>().CredentialResolution
            .Should().Be(SupplierCredentialResolution.PlatformOnly);
    }

    [Fact]
    public void Per_agency_credentials_are_one_setting_away()
    {
        using var provider = Build(new Dictionary<string, string?> { ["Suppliers:CredentialResolution"] = "AgencyFirst" });

        provider.GetRequiredService<SupplierCredentialOptions>().CredentialResolution
            .Should().Be(SupplierCredentialResolution.AgencyFirst);
    }

    // ------------------------------------------------------------------ never logged

    [Fact]
    public void Decrypted_credentials_never_print_their_secrets()
    {
        // A record's generated ToString prints every property; one structured log line of this
        // would otherwise put the merchant key in the logs.
        var credentials = new SupplierCredentials(
            Guid.CreateVersion7(), "trips_africa", SupplierEnvironment.Production, null,
            "ACCESS", "mk_live_super_secret", "bearer_super_secret");

        var printed = credentials.ToString();

        printed.Should().NotContain("mk_live_super_secret").And.NotContain("bearer_super_secret");
        printed.Should().Contain("ACCESS").And.Contain("platform").And.Contain("[redacted]");
        $"{credentials}".Should().Be(printed);
    }

    [Fact]
    public void A_travel_document_on_its_way_to_a_supplier_never_prints_its_number()
    {
        var document = new SupplierTravelDocument(TravelDocumentKind.Passport, "A01234567", "NG");

        document.ToString().Should().NotContain("A01234567").And.Contain("[redacted]");
    }

    // ------------------------------------------------------------------ partition maintenance

    [Fact]
    public void Call_log_maintenance_is_scheduled_daily_in_UTC_through_its_interface()
    {
        var recurringJobs = new RecordingRecurringJobManager();

        SupplierApiCallMaintenanceSchedule.Register(recurringJobs);

        var registration = recurringJobs.Registrations.Should().ContainSingle().Subject;
        registration.Id.Should().Be("supplier-api-call-maintenance");
        registration.Cron.Should().Be("15 3 * * *");
        registration.Options.TimeZone.Should().Be(TimeZoneInfo.Utc);
        registration.Job.Type.Should().Be<ISupplierApiCallMaintenance>();
    }

    [Fact]
    public void The_default_retention_keeps_at_least_the_planned_ninety_days()
    {
        // Whole months are dropped, so three months kept means between 90 and 120 days of history.
        var options = new SupplierApiCallOptions();

        options.RetentionMonths.Should().BeGreaterThanOrEqualTo(3);
        options.PartitionsCreatedAhead.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void A_zero_retention_is_refused_at_startup()
    {
        using var provider = Build(new Dictionary<string, string?> { ["SupplierApiCalls:RetentionMonths"] = "0" });

        var read = () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SupplierApiCallOptions>>().Value;

        read.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>();
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        settings[$"ConnectionStrings:{DependencyInjection.PostgresConnectionName}"] = UnusedConnectionString;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection().AddInfrastructure(configuration).BuildServiceProvider();
    }

    private sealed class RecordingRecurringJobManager : IRecurringJobManager
    {
        public List<(string Id, Job Job, string Cron, RecurringJobOptions Options)> Registrations { get; } = [];

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) =>
            Registrations.Add((recurringJobId, job, cronExpression, options));

        public void Trigger(string recurringJobId) => throw new NotSupportedException();

        public void RemoveIfExists(string recurringJobId) => throw new NotSupportedException();
    }
}
