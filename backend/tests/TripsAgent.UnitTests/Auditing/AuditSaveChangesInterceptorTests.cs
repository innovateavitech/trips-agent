using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TripsAgent.Domain.Auditing;
using TripsAgent.Infrastructure.Auditing;

namespace TripsAgent.UnitTests.Auditing;

/// <summary>
/// The point of writing the audit trail from the save pipeline is that nobody has to remember to
/// do it. These tests hold that promise to account: save an audited entity by any route and a
/// record appears, with the actor attached and the secrets stripped.
/// </summary>
public sealed class AuditSaveChangesInterceptorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 14, 30, 0, TimeSpan.Zero);

    private readonly StubAuditContext auditContext = new();
    private readonly AuditTestDbContext context;
    private readonly Microsoft.Data.Sqlite.SqliteConnection connection;

    public AuditSaveChangesInterceptorTests()
    {
        var interceptor = new AuditSaveChangesInterceptor(
            auditContext,
            new AuditRedactionPolicy(),
            new FixedTimeProvider(Now));

        (context, connection) = AuditTestDbContext.Create(interceptor);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    [Fact]
    public async Task Inserting_an_audited_entity_should_record_it_as_created()
    {
        await SaveAgencyAsync();

        var entry = await SingleAuditEntryAsync();

        entry.Action.Should().Be(AuditActions.Created);
        entry.EntityType.Should().Be(nameof(AuditedAgency));
        entry.OccurredAt.Should().Be(Now);
        entry.BeforeState.Should().BeNull("nothing existed before an insert");
        entry.AfterState.Should().NotBeNull();
    }

    [Fact]
    public async Task Updating_an_audited_entity_should_record_only_what_changed()
    {
        var agency = await SaveAgencyAsync();
        await ClearAuditLogAsync();

        agency.Status = "verified";
        await context.SaveChangesAsync();

        var entry = await SingleAuditEntryAsync();
        entry.Action.Should().Be(AuditActions.Updated);

        var before = Parse(entry.BeforeState);
        var after = Parse(entry.AfterState);

        before.Should().ContainKey(nameof(AuditedAgency.Status)).WhoseValue.Should().Be("pending");
        after.Should().ContainKey(nameof(AuditedAgency.Status)).WhoseValue.Should().Be("verified");

        // The columns that did not move are absent, so the reader is not left diffing two large
        // objects by eye to find the one field that changed.
        before.Should().NotContainKey(nameof(AuditedAgency.LegalName));
    }

    [Fact]
    public async Task Deleting_an_audited_entity_should_record_the_whole_row_it_removed()
    {
        var agency = await SaveAgencyAsync();
        await ClearAuditLogAsync();

        context.Agencies.Remove(agency);
        await context.SaveChangesAsync();

        var entry = await SingleAuditEntryAsync();

        entry.Action.Should().Be(AuditActions.Deleted);
        entry.AfterState.Should().BeNull("nothing exists after a delete");
        Parse(entry.BeforeState).Should().ContainKey(nameof(AuditedAgency.LegalName));
    }

    [Fact]
    public async Task Saving_an_entity_that_did_not_opt_in_should_record_nothing()
    {
        context.Notes.Add(new UnauditedNote { Text = "just a note" });
        await context.SaveChangesAsync();

        (await context.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_update_that_changes_nothing_should_record_nothing()
    {
        var agency = await SaveAgencyAsync();
        await ClearAuditLogAsync();

        agency.Status = agency.Status;
        context.Entry(agency).State = EntityState.Modified;
        await context.SaveChangesAsync();

        (await context.AuditLogs.CountAsync()).Should().Be(0,
            "a row rewritten with its own values is not a change worth recording");
    }

    [Fact]
    public async Task The_actor_should_be_taken_from_the_ambient_context()
    {
        var actor = Guid.CreateVersion7();
        auditContext.ActorUserId = actor;
        auditContext.ActorType = AuditActorType.PlatformAdmin;
        auditContext.ActorIpAddress = "197.210.0.1";
        auditContext.CorrelationId = "req-42";
        auditContext.SetReason("Suspended pending KYB review");

        await SaveAgencyAsync();

        var entry = await SingleAuditEntryAsync();

        entry.ActorUserId.Should().Be(actor);
        entry.ActorType.Should().Be(AuditActorType.PlatformAdmin);
        entry.ActorIpAddress.Should().Be("197.210.0.1");
        entry.CorrelationId.Should().Be("req-42");
        entry.Reason.Should().Be("Suspended pending KYB review");
    }

    [Fact]
    public async Task A_password_hash_should_never_reach_the_audit_log()
    {
        await SaveAgencyAsync(passwordHash: "$2a$12$abcdefghijklmnopqrstuv");

        var entry = await SingleAuditEntryAsync();

        entry.AfterState.Should().NotContain("2a$12", "the hash itself must not be stored");
        Parse(entry.AfterState)[nameof(AuditedAgency.PasswordHash)]
            .Should().Be(AuditRedactionPolicy.RedactedPlaceholder);
    }

    [Fact]
    public async Task A_passport_number_should_be_stored_only_in_part()
    {
        await SaveAgencyAsync(passportNumber: "A01234567");

        var entry = await SingleAuditEntryAsync();

        Parse(entry.AfterState)[nameof(AuditedAgency.PassportNumber)].Should().Be("*****4567");
    }

    [Fact]
    public async Task The_row_should_be_attributed_to_the_agency_it_belongs_to()
    {
        // Not to whoever is signed in: a platform admin acting on an agency's record should
        // still produce a row that agency can see in its own trail.
        var owningAgency = Guid.CreateVersion7();
        auditContext.AgencyId = Guid.CreateVersion7();
        auditContext.ActorType = AuditActorType.PlatformAdmin;

        await SaveAgencyAsync(agencyId: owningAgency);

        (await SingleAuditEntryAsync()).AgencyId.Should().Be(owningAgency);
    }

    [Fact]
    public async Task Saving_several_audited_entities_at_once_should_record_each_of_them()
    {
        context.Agencies.Add(new AuditedAgency { LegalName = "Kano Travels" });
        context.Agencies.Add(new AuditedAgency { LegalName = "Lagos Tours" });
        await context.SaveChangesAsync();

        (await context.AuditLogs.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_save_that_failed_and_is_tried_again_should_be_recorded_once()
    {
        // EF leaves everything in the change tracker when SaveChanges throws — including the audit
        // rows the interceptor added. Without a guard, fixing the problem and saving again would
        // write the stale row from the failed attempt as well as the real one.
        var agency = new AuditedAgency { AgencyId = Guid.CreateVersion7(), LegalName = null! };
        context.Agencies.Add(agency);

        var firstAttempt = async () => await context.SaveChangesAsync();
        await firstAttempt.Should().ThrowAsync<DbUpdateException>("LegalName is NOT NULL");

        agency.LegalName = "Kano Travels Ltd";
        await context.SaveChangesAsync();

        var entry = await SingleAuditEntryAsync();
        Parse(entry.AfterState)[nameof(AuditedAgency.LegalName)].Should().Be("Kano Travels Ltd",
            "the row describes the save that succeeded, not the attempt that failed");
    }

    private async Task<AuditedAgency> SaveAgencyAsync(
        Guid? agencyId = null,
        string passwordHash = "",
        string passportNumber = "")
    {
        var agency = new AuditedAgency
        {
            AgencyId = agencyId ?? Guid.CreateVersion7(),
            LegalName = "Kano Travels Ltd",
            PasswordHash = passwordHash,
            PassportNumber = passportNumber,
        };

        context.Agencies.Add(agency);
        await context.SaveChangesAsync();

        return agency;
    }

    private async Task<AuditLogEntry> SingleAuditEntryAsync() =>
        await context.AuditLogs.AsNoTracking().SingleAsync();

    private async Task ClearAuditLogAsync()
    {
        // Only in this test double. The real table refuses deletes outright — see
        // TripsAgent.IntegrationTests.
        // Note there is no ChangeTracker.Clear() here: clearing it would detach the entity
        // the caller is about to modify, and the update would then go unnoticed by EF entirely.
        context.AuditLogs.RemoveRange(await context.AuditLogs.ToListAsync());
        await context.SaveChangesAsync();
    }

    private static Dictionary<string, string> Parse(string? json)
    {
        json.Should().NotBeNull();

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json!)!
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);
    }
}
