using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// What the back-office console needs on top of what is already there (epic 66).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things, and no new table. <c>platform.admin_alerts</c> and
    /// <c>platform.audit_logs</c> already exist and already carry everything the dashboard and
    /// the audit viewer read, so the console reads them rather than duplicating them.
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <c>tenancy.agencies</c> gains <c>status_changed_at</c> and <c>status_reason</c>, so a
    ///     screen showing a suspended agency can say since when and why without reading a monthly
    ///     partition of the audit log for one banner.
    ///   </item>
    ///   <item>
    ///     <c>identity.user_roles.agency_id</c> becomes nullable. A null agency is a Trips
    ///     back-office grant. Nothing about row-level security changes and nothing needs to: the
    ///     existing <c>tenant_isolation</c> policy is
    ///     <c>platform_scope_active() OR agency_id = current_agency_id()</c>, and a NULL never
    ///     equals anything — so a platform grant is invisible to every agency, both for reading
    ///     and, through the same policy's WITH CHECK, for writing. Platform users were previously
    ///     granted their role against whichever agency the seeder happened to create first, which
    ///     read in the audit trail as though Trips' own admins worked there, and was impossible
    ///     on a fresh install with no agency yet.
    ///   </item>
    ///   <item>
    ///     A unique index for those grants. PostgreSQL treats NULLs as distinct, so the existing
    ///     (user, role, agency) unique index stops constraining a row once agency_id is NULL.
    ///   </item>
    /// </list>
    /// </remarks>
    public partial class AddAdminConsole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "agency_id",
                schema: "identity",
                table: "user_roles",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "status_changed_at",
                schema: "tenancy",
                table: "agencies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_reason",
                schema: "tenancy",
                table: "agencies",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_platform_user_id_role_id",
                schema: "identity",
                table: "user_roles",
                columns: new[] { "user_id", "role_id" },
                unique: true,
                filter: "agency_id IS NULL");

            // ------------------------------------------------ the grants that were already wrong
            //
            // Run before the index above could have been violated by them: a platform user cannot
            // hold the same role twice, so nulling their agency cannot collide. The join is on
            // users.agency_id IS NULL, which is the definition of a Trips back-office account.
            migrationBuilder.Sql("""
                UPDATE identity.user_roles AS ur
                   SET agency_id = NULL
                  FROM identity.users AS u
                 WHERE u.id = ur.user_id
                   AND u.agency_id IS NULL
                   AND ur.agency_id IS NOT NULL;
                """);

            // ------------------------------------------------------------------ status reasons
            //
            // A status change has to be explained, and an empty string is not an explanation.
            // The application refuses one too; this is the backstop for anything reaching the
            // table another way.
            migrationBuilder.Sql("""
                ALTER TABLE tenancy.agencies
                    ADD CONSTRAINT ck_agencies_status_reason_not_blank
                        CHECK (status_reason IS NULL OR btrim(status_reason) <> ''),
                    ADD CONSTRAINT ck_agencies_status_change_is_explained
                        CHECK ((status_changed_at IS NULL) = (status_reason IS NULL));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE tenancy.agencies
                    DROP CONSTRAINT IF EXISTS ck_agencies_status_change_is_explained,
                    DROP CONSTRAINT IF EXISTS ck_agencies_status_reason_not_blank;
                """);

            // Going back means agency_id is NOT NULL again, and a platform grant has no agency to
            // put there. They are deleted rather than pointed at an arbitrary agency: a wrong
            // grant is worse than a missing one, and re-seeding restores them.
            migrationBuilder.Sql("DELETE FROM identity.user_roles WHERE agency_id IS NULL;");

            migrationBuilder.DropIndex(
                name: "ix_user_roles_platform_user_id_role_id",
                schema: "identity",
                table: "user_roles");

            migrationBuilder.DropColumn(
                name: "status_changed_at",
                schema: "tenancy",
                table: "agencies");

            migrationBuilder.DropColumn(
                name: "status_reason",
                schema: "tenancy",
                table: "agencies");

            migrationBuilder.AlterColumn<Guid>(
                name: "agency_id",
                schema: "identity",
                table: "user_roles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
