using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TripsAgent.Application.Retention;
using TripsAgent.Infrastructure.Retention;

namespace TripsAgent.UnitTests.Retention;

/// <summary>What the Worker schedules, and which settings stop it starting.</summary>
public class DataRetentionRegistrationTests
{
    [Fact]
    public void The_job_is_scheduled_daily_in_UTC_through_its_interface()
    {
        var recurringJobs = new RecordingRecurringJobManager();

        DataRetentionSchedule.Register(recurringJobs);

        var registration = recurringJobs.Registrations.Should().ContainSingle().Subject;
        registration.Id.Should().Be("data-retention");
        registration.Cron.Should().Be("10 3 * * *");
        registration.Options.TimeZone.Should().Be(TimeZoneInfo.Utc);

        // Through the interface, so each run resolves a fresh scope and DbContext.
        registration.Job.Type.Should().Be<IDataRetentionPurge>();
        registration.Job.Method.Name.Should().Be(nameof(IDataRetentionPurge.RunAsync));
    }

    [Fact]
    public void With_nothing_configured_the_job_is_a_dry_run()
    {
        var options = Options(new Dictionary<string, string?>());

        options.DryRun.Should().BeTrue();
    }

    [Fact]
    public void Deleting_for_real_takes_an_explicit_false()
    {
        Options(new Dictionary<string, string?> { ["DataRetention:DryRun"] = "false" }).DryRun.Should().BeFalse();
    }

    [Theory]
    [InlineData("DataRetention:LoginAttemptDays", "0")]
    [InlineData("DataRetention:TravelDocumentDays", "-1")]
    [InlineData("DataRetention:NotificationDays", "0")]
    public void A_window_under_one_day_is_refused(string key, string value)
    {
        var act = () => Options(new Dictionary<string, string?> { [key] = value });

        act.Should().Throw<OptionsValidationException>().WithMessage("*at least 1 day*");
    }

    [Fact]
    public void Forgetting_processed_messages_sooner_than_a_week_is_refused()
    {
        var act = () => Options(new Dictionary<string, string?> { ["DataRetention:ProcessedMessageDays"] = "3" });

        act.Should().Throw<OptionsValidationException>().WithMessage("*ProcessedMessageDays*");
    }

    private static DataRetentionOptions Options(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        using var provider = new ServiceCollection()
            .AddDataRetention(configuration)
            .BuildServiceProvider();

        return provider.GetRequiredService<IOptions<DataRetentionOptions>>().Value;
    }

    private sealed class RecordingRecurringJobManager : IRecurringJobManager
    {
        public List<(string Id, Job Job, string Cron, RecurringJobOptions Options)> Registrations { get; } = [];

        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) =>
            Registrations.Add((recurringJobId, job, cronExpression, options));

        public void Trigger(string recurringJobId) =>
            throw new NotSupportedException("Registration tests never trigger a job.");

        public void RemoveIfExists(string recurringJobId) =>
            throw new NotSupportedException("Registration tests never remove a job.");
    }
}
