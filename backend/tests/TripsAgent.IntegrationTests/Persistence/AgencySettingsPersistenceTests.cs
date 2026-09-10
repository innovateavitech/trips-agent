using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TripsAgent.Domain.Tenancy;
using TripsAgent.Infrastructure.Persistence;

namespace TripsAgent.IntegrationTests.Persistence;

/// <summary>
/// Covers the two satellite tables and, in particular, that the jsonb columns actually
/// round-trip — a list mapped to jsonb is the kind of thing that compiles, generates a
/// plausible migration, and then throws the first time anyone saves a row.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AgencySettingsPersistenceTests
{
    private readonly PostgresFixture _postgres;

    public AgencySettingsPersistenceTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task Settings_round_trip_including_the_jsonb_currency_list()
    {
        await using var context = await MigratedDatabaseAsync();

        var agency = NewPrincipal("settings-agency");
        var settings = AgencySettings.CreateDefault(agency);
        settings.SetSupportedCurrencies(["USD", "GBP"], agency.BaseCurrency);

        context.Agencies.Add(agency);
        context.AgencySettings.Add(settings);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        var reloaded = await context.AgencySettings.SingleAsync(s => s.AgencyId == agency.Id);

        reloaded.SupportedCurrencies.Should().Equal("GBP", "NGN", "USD");
        reloaded.InvoicePrefix.Should().Be("INV-SETTIN");
        reloaded.BookingReferencePrefix.Should().Be("SETTIN");
    }

    [Fact]
    public async Task The_currency_list_really_is_jsonb_in_the_database()
    {
        await using var context = await MigratedDatabaseAsync();

        var agency = NewPrincipal("jsonb-agency");
        context.Agencies.Add(agency);
        context.AgencySettings.Add(AgencySettings.CreateDefault(agency));
        await context.SaveChangesAsync();

        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        // If this were a text column pretending to be JSON, jsonb_array_length would error.
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT jsonb_array_length(supported_currencies), supported_currencies ->> 0
            FROM tenancy.agency_settings
            LIMIT 1
            """;

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        reader.GetInt32(0).Should().Be(1);
        reader.GetString(1).Should().Be("NGN");
    }

    [Fact]
    public async Task Branding_round_trips()
    {
        await using var context = await MigratedDatabaseAsync();

        var agency = NewPrincipal("branding-agency");
        var branding = AgencyBranding.CreateDefault(agency);
        branding.SetColors("#325DEC", "#FF9900");
        branding.SetContactAddress("12 Awolowo Road, Ikoyi, Lagos");

        context.Agencies.Add(agency);
        context.AgencyBranding.Add(branding);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        var reloaded = await context.AgencyBranding.SingleAsync(b => b.AgencyId == agency.Id);

        reloaded.PrimaryColor.Should().Be("#325DEC");
        reloaded.SecondaryColor.Should().Be("#FF9900");
        reloaded.ContactAddress.Should().Be("12 Awolowo Road, Ikoyi, Lagos");
        reloaded.FontFamily.Should().Be(AgencyBranding.DefaultFontFamily);
    }

    [Fact]
    public async Task The_database_rejects_a_malformed_colour()
    {
        await using var context = await MigratedDatabaseAsync();

        var agency = NewPrincipal("colour-agency");
        context.Agencies.Add(agency);
        context.AgencyBranding.Add(AgencyBranding.CreateDefault(agency));
        await context.SaveChangesAsync();

        // Around the domain, the way a migration or a hand-written UPDATE would.
        var act = async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE tenancy.agency_branding SET primary_color = 'rebecca' WHERE agency_id = {agency.Id}");

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ck_agency_branding_primary_color");
    }

    [Fact]
    public async Task Adding_a_second_settings_row_through_EF_replaces_the_first()
    {
        await using var context = await MigratedDatabaseAsync();

        var agency = NewPrincipal("single-settings");
        context.Agencies.Add(agency);
        context.AgencySettings.Add(AgencySettings.CreateDefault(agency));
        await context.SaveChangesAsync();

        var replacement = AgencySettings.CreateDefault(agency);
        context.AgencySettings.Add(replacement);
        await context.SaveChangesAsync();

        // Worth knowing, because it is not what you would guess: on a required one-to-one, EF
        // treats the new dependent as replacing the old one — it deletes the orphan and inserts
        // the replacement, so no unique-index violation ever reaches PostgreSQL.
        (await context.AgencySettings.CountAsync()).Should().Be(1);

        context.ChangeTracker.Clear();
        var surviving = await context.AgencySettings.SingleAsync();
        surviving.Id.Should().Be(replacement.Id);
    }

    [Fact]
    public async Task The_database_still_refuses_a_duplicate_settings_row()
    {
        await using var context = await MigratedDatabaseAsync();

        var agency = NewPrincipal("duplicate-settings");
        context.Agencies.Add(agency);
        context.AgencySettings.Add(AgencySettings.CreateDefault(agency));
        await context.SaveChangesAsync();

        // EF's replacement behaviour is convenient but it is not the guarantee — a seed script
        // or a migration inserting directly has to be stopped by the index itself.
        var act = async () => await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO tenancy.agency_settings
                 (id, agency_id, supported_currencies, invoice_prefix,
                  booking_reference_prefix, notification_preferences, created_at, updated_at)
             VALUES
                 ({Guid.CreateVersion7()}, {agency.Id}, '["NGN"]'::jsonb, 'INV-DUP',
                  'DUP', jsonb_build_object(), now(), now())
             """);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("ix_agency_settings_agency_id");
    }

    [Fact]
    public async Task Deleting_an_agency_takes_its_settings_and_branding_with_it()
    {
        await using var context = await MigratedDatabaseAsync();

        var agency = NewPrincipal("cascade-agency");
        context.Agencies.Add(agency);
        context.AgencySettings.Add(AgencySettings.CreateDefault(agency));
        context.AgencyBranding.Add(AgencyBranding.CreateDefault(agency));
        await context.SaveChangesAsync();

        // Cascade is right here and Restrict is right for sub-agents: settings and branding are
        // parts of the agency, whereas a sub-agent is an independent business.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM tenancy.agencies WHERE id = {agency.Id}");

        (await context.AgencySettings.CountAsync()).Should().Be(0);
        (await context.AgencyBranding.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_seeder_creates_a_principal_with_one_sub_agent()
    {
        await using var context = await MigratedDatabaseAsync();

        var created = await DatabaseSeeder.SeedAsync(context);

        created.Should().Be(2);

        context.ChangeTracker.Clear();

        var principal = await context.Agencies.SingleAsync(a => a.Slug == DatabaseSeeder.PrincipalSlug);
        var subAgent = await context.Agencies.SingleAsync(a => a.Slug == DatabaseSeeder.SubAgentSlug);

        principal.Type.Should().Be(AgencyType.Principal);
        subAgent.Type.Should().Be(AgencyType.SubAgent);
        subAgent.ParentAgencyId.Should().Be(principal.Id);
        subAgent.Path.Should().StartWith(principal.Path);

        // Each gets its own settings and branding row.
        (await context.AgencySettings.CountAsync()).Should().Be(2);
        (await context.AgencyBranding.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Seeding_twice_creates_nothing_the_second_time()
    {
        await using var context = await MigratedDatabaseAsync();

        await DatabaseSeeder.SeedAsync(context);
        var second = await DatabaseSeeder.SeedAsync(context);

        second.Should().Be(0);
        (await context.Agencies.CountAsync()).Should().Be(2);
    }

    private static Agency NewPrincipal(string slug) =>
        Agency.RegisterPrincipal($"{slug} Limited", slug, "NG", "NGN", "Africa/Lagos");

    private async Task<AppDbContext> MigratedDatabaseAsync([CallerMemberName] string testName = "")
    {
        var name = testName.ToLowerInvariant();
        var context = await _postgres.CreateEmptyDatabaseAsync(name[..Math.Min(name.Length, 60)]);
        await context.Database.MigrateAsync();
        return context;
    }
}
