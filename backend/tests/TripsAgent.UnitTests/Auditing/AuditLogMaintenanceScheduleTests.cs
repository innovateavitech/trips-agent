using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using TripsAgent.Application.Auditing;
using TripsAgent.Infrastructure.Auditing;

namespace TripsAgent.UnitTests.Auditing;

/// <summary>
/// The retention policy is only a policy if something runs it. These pin down what the Worker
/// registers with Hangfire, without starting Hangfire: a recording manager stands in for the real
/// one, which would need a live PostgreSQL to hold the schedule.
/// </summary>
public class AuditLogMaintenanceScheduleTests
{
    [Fact]
    public void Register_should_schedule_maintenance_daily_in_UTC()
    {
        var recurringJobs = new RecordingRecurringJobManager();

        AuditLogMaintenanceSchedule.Register(recurringJobs);

        var registration = recurringJobs.Registrations.Should().ContainSingle().Subject;

        registration.Id.Should().Be(AuditLogMaintenanceSchedule.JobId);
        registration.Cron.Should().Be("0 3 * * *", "daily at 03:00, so a failure is retried tomorrow");
        registration.Options.TimeZone.Should().Be(TimeZoneInfo.Utc,
            "a schedule in the server's local zone would drift the day someone moves the server");
    }

    [Fact]
    public void Register_should_invoke_the_maintenance_service_through_its_interface()
    {
        // Through the interface, so Hangfire resolves it from the container per run — with a fresh
        // DbContext — rather than holding one instance for the life of the Worker.
        var recurringJobs = new RecordingRecurringJobManager();

        AuditLogMaintenanceSchedule.Register(recurringJobs);

        var job = recurringJobs.Registrations.Single().Job;
        job.Type.Should().Be<IAuditLogMaintenance>();
        job.Method.Name.Should().Be(nameof(IAuditLogMaintenance.RunAsync));
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
