using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PostgreSQL row-level security as a backstop behind the EF tenant filters. Issue #12,
    /// ADR-0006.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The EF query filters are the first line of tenant isolation; this is the second. Every
    /// policy below mirrors a filter in <c>AppDbContext</c>, so a query the application runs
    /// legitimately returns the same rows either way — and one that has lost its filter still
    /// cannot see another agency's.
    /// </para>
    /// <para>
    /// A policy reads two settings, written per connection by <c>TenantSessionInterceptor</c>:
    /// <c>app.agency_id</c> and <c>app.platform_scope</c>. Unset means no tenant and no scope, and
    /// the policies then return nothing — they fail closed.
    /// </para>
    /// <para>
    /// None of this applies to a superuser or a BYPASSRLS role, which is why the application must
    /// connect as <c>tripsagent_app</c> and migrations as a separate role that can bypass.
    /// </para>
    /// </remarks>
    public partial class AddRowLevelSecurity : Migration
    {
        /// <summary>The runtime role. No superuser, no BYPASSRLS — the point of the exercise.</summary>
        internal const string ApplicationRole = "tripsagent_app";

        /// <summary>Tables owned by one agency through their <c>agency_id</c> column.</summary>
        internal static readonly string[] AgencyOwnedTables =
        [
            "tenancy.agency_branding",
            "tenancy.agency_settings",
            "tenancy.kyb_documents",
            "tenancy.kyb_submissions",
            "identity.user_roles",
            "identity.users",
            "identity.user_invitations",
            "payments.payment_transactions",
            "payments.wallets",
            "payments.wallet_holds",
            "payments.wallet_transactions",

            // No EF filter exists on these two: platform accounts carry a null agency, so they do
            // not fit ITenantScoped. Here they get one anyway — a null-agency row is visible only
            // inside a platform scope, which is where every ledger write already happens.
            "payments.ledger_accounts",
            "payments.ledger_entries",
        ];

        /// <summary>Credentials, owned through their <c>user_id</c> rather than an agency.</summary>
        internal static readonly string[] UserOwnedTables =
        [
            "identity.refresh_tokens",
            "identity.password_reset_tokens",
            "identity.otp_codes",
        ];

        // Wrapped in SELECT so PostgreSQL evaluates each once per query as an InitPlan, not once
        // per row — the difference between a policy that is free and one that is a full scan's
        // worth of function calls.
        private const string PlatformScope = "(SELECT tenancy.platform_scope_active())";
        private const string CurrentAgency = "(SELECT tenancy.current_agency_id())";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // ------------------------------------------------------------------ the role
            // NOLOGIN here: the password is provisioned outside migrations, because a secret in a
            // migration is a secret in git history. Roles are server-wide, so two databases on one
            // server migrating at once can race to create it; the loser's error is harmless.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tripsagent_app') THEN
                        CREATE ROLE tripsagent_app NOLOGIN NOSUPERUSER NOBYPASSRLS;
                    END IF;
                EXCEPTION
                    WHEN duplicate_object THEN NULL;
                END
                $$;
                """);

            // The integrity triggers below run as their owner, and must see every row. That holds
            // only when the owner bypasses row-level security. Warn loudly rather than fail: some
            // managed databases need a support ticket to grant BYPASSRLS, and a warning is the
            // honest middle ground. ADR-0006 has the detail.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_roles
                         WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE WARNING
                            'Migrating as %, which does not bypass row-level security. Integrity triggers '
                            'run as their owner and must see every row: migrate as a superuser or a '
                            'BYPASSRLS role. See docs/adr/0006.', current_user;
                    END IF;
                END
                $$;
                """);

            // ------------------------------------------------------------------ the settings
            // NULLIF turns an empty setting into NULL, and NULL matches nothing — so an unset or
            // cleared tenant sees no rows at all. A malformed value fails the cast and the query
            // with it, which is also closed.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION tenancy.current_agency_id()
                RETURNS uuid
                LANGUAGE sql STABLE PARALLEL SAFE
                AS $fn$ SELECT NULLIF(current_setting('app.agency_id', true), '')::uuid $fn$;

                CREATE OR REPLACE FUNCTION tenancy.platform_scope_active()
                RETURNS boolean
                LANGUAGE sql STABLE PARALLEL SAFE
                AS $fn$ SELECT coalesce(current_setting('app.platform_scope', true), '') = 'on' $fn$;
                """);

            // ------------------------------------------------------------------ privileges
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    EXECUTE format('GRANT CONNECT ON DATABASE %I TO tripsagent_app', current_database());
                END
                $$;

                GRANT USAGE ON SCHEMA public, tenancy, identity, payments, platform TO tripsagent_app;

                GRANT SELECT, INSERT, UPDATE, DELETE
                   ON ALL TABLES IN SCHEMA tenancy, identity, payments, platform TO tripsagent_app;
                GRANT USAGE, SELECT, UPDATE
                   ON ALL SEQUENCES IN SCHEMA tenancy, identity, payments, platform TO tripsagent_app;

                -- Tables created by later migrations get the same, so a new table is usable the
                -- moment it exists rather than the moment someone remembers to grant it.
                ALTER DEFAULT PRIVILEGES IN SCHEMA tenancy, identity, payments, platform
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tripsagent_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA tenancy, identity, payments, platform
                    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO tripsagent_app;

                -- Read-only, so the seed command can refuse to run against an unmigrated database.
                GRANT SELECT ON TABLE public."__EFMigrationsHistory" TO tripsagent_app;

                -- The blanket grant above included UPDATE and DELETE on the two append-only tables.
                -- The earlier REVOKEs were conditional on this role existing, and it did not until
                -- now — so they have been no-ops. Re-asserted, and this time they bite.
                REVOKE UPDATE, DELETE ON payments.ledger_entries FROM tripsagent_app;
                REVOKE UPDATE, DELETE ON platform.audit_logs FROM tripsagent_app;

                -- Hangfire runs as this role and creates its own tables on first start, so it
                -- needs to create inside its schema and nowhere else.
                CREATE SCHEMA IF NOT EXISTS hangfire;
                GRANT USAGE, CREATE ON SCHEMA hangfire TO tripsagent_app;
                GRANT ALL ON ALL TABLES IN SCHEMA hangfire TO tripsagent_app;
                GRANT ALL ON ALL SEQUENCES IN SCHEMA hangfire TO tripsagent_app;
                """);

            // ------------------------------------------------------------------ integrity triggers
            // These read rows to check or derive other rows. Run as the caller, they would see
            // only the caller's tenant — the balance check would sum part of a transaction group
            // and could accept one that is unbalanced overall; the path trigger would fail to find
            // a sub-agent's parent. As definer they see the whole table, as integrity checks must.
            migrationBuilder.Sql(
                """
                ALTER FUNCTION payments.assert_ledger_group_balanced() SECURITY DEFINER;
                ALTER FUNCTION tenancy.agencies_compute_path() SECURITY DEFINER SET search_path = tenancy, public;
                ALTER FUNCTION tenancy.agencies_reparent_descendants() SECURITY DEFINER SET search_path = tenancy, public;
                """);

            // ------------------------------------------------------------------ policies
            foreach (var table in AgencyOwnedTables)
            {
                EnableFor(migrationBuilder, table,
                    @using: $"{PlatformScope} OR agency_id = {CurrentAgency}",
                    check: $"{PlatformScope} OR agency_id = {CurrentAgency}");
            }

            // An agency sees itself and, as a principal, its own sub-agents — exactly the EF filter.
            EnableFor(migrationBuilder, "tenancy.agencies",
                @using: $"{PlatformScope} OR id = {CurrentAgency} OR parent_agency_id = {CurrentAgency}",
                check: $"{PlatformScope} OR id = {CurrentAgency} OR parent_agency_id = {CurrentAgency}");

            // System roles (null agency) are visible to everyone — they are the roles we ship.
            // Only the platform may create or change one; an agency may only write its own.
            EnableFor(migrationBuilder, "identity.roles",
                @using: $"{PlatformScope} OR agency_id = {CurrentAgency} OR agency_id IS NULL",
                check: $"{PlatformScope} OR agency_id = {CurrentAgency}");

            // Credentials follow their user's agency. The subquery is itself subject to the users
            // policy, so it composes rather than widening anything.
            foreach (var table in UserOwnedTables)
            {
                var ownersAgency =
                    $"EXISTS (SELECT 1 FROM identity.users u WHERE u.id = user_id AND u.agency_id = {CurrentAgency})";

                EnableFor(migrationBuilder, table,
                    @using: $"{PlatformScope} OR {ownersAgency}",
                    check: $"{PlatformScope} OR {ownersAgency}");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Policies first: they depend on the functions dropped below.
            foreach (var table in AgencyOwnedTables.Concat(UserOwnedTables).Append("tenancy.agencies").Append("identity.roles"))
            {
                migrationBuilder.Sql(
                    $"""
                     DROP POLICY IF EXISTS tenant_isolation ON {table};
                     ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;
                     ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;
                     """);
            }

            migrationBuilder.Sql(
                """
                ALTER FUNCTION payments.assert_ledger_group_balanced() SECURITY INVOKER;
                ALTER FUNCTION tenancy.agencies_compute_path() SECURITY INVOKER;
                ALTER FUNCTION tenancy.agencies_reparent_descendants() SECURITY INVOKER;

                DROP FUNCTION IF EXISTS tenancy.current_agency_id();
                DROP FUNCTION IF EXISTS tenancy.platform_scope_active();
                """);

            // The role and its grants stay. Roles are server-wide and may serve other databases,
            // and undoing the grants would reopen the append-only tables to UPDATE and DELETE.
        }

        private static void EnableFor(MigrationBuilder migrationBuilder, string table, string @using, string check) =>
            migrationBuilder.Sql(
                $"""
                 ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;

                 -- FORCE: without it the table's owner is exempt, and a deployment that connects as
                 -- the owner would be silently unpoliced.
                 ALTER TABLE {table} FORCE ROW LEVEL SECURITY;

                 CREATE POLICY tenant_isolation ON {table}
                     USING ({@using})
                     WITH CHECK ({check});
                 """);
    }
}
