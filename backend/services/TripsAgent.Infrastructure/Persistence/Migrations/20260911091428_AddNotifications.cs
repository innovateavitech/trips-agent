using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Notifications, their versioned templates and the email suppression list. Issue #45.
    /// </summary>
    /// <remarks>
    /// A new schema, so the application role needs granting on it: the default privileges set up in
    /// AddRowLevelSecurity name the schemas that existed then, and this one did not. Only
    /// <c>notifications.notifications</c> belongs to an agency, so only it gets a policy — the
    /// templates and the suppression list are platform-wide by design.
    /// </remarks>
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "notification_templates",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    locale = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_template = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    html_template = table.Column<string>(type: "text", nullable: false),
                    text_template = table.Column<string>(type: "text", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    locale = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipient_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipient_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    recipient_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    template_version = table.Column<int>(type: "integer", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_message_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.CheckConstraint("ck_notifications_status", "status IN ('queued', 'sending', 'sent', 'delivered', 'failed', 'bounced')");
                    table.ForeignKey(
                        name: "fk_notifications_agencies_agency_id",
                        column: x => x.agency_id,
                        principalSchema: "tenancy",
                        principalTable: "agencies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "suppressed_email_addresses",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "citext", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    suppressed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    discovered_by_notification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lifted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suppressed_email_addresses", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_templates_key_channel_locale_version",
                schema: "notifications",
                table: "notification_templates",
                columns: new[] { "key", "channel", "locale", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_agency_id_created_at",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "agency_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_agency_id_dedupe_key",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "agency_id", "dedupe_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_provider_message_id",
                schema: "notifications",
                table: "notifications",
                column: "provider_message_id",
                filter: "provider_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_suppressed_email_addresses_active",
                schema: "notifications",
                table: "suppressed_email_addresses",
                column: "address",
                unique: true,
                filter: "lifted_at IS NULL");

            migrationBuilder.Sql(
                """
                GRANT USAGE ON SCHEMA notifications TO tripsagent_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA notifications TO tripsagent_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA notifications
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tripsagent_app;
                """);

            // The same policy every agency-owned table has (ADR-0006): an agency sees its own
            // notification history, including mail to its travellers, and never another agency's.
            // The dispatcher reads across agencies inside the platform scope.
            migrationBuilder.Sql(
                """
                ALTER TABLE notifications.notifications ENABLE ROW LEVEL SECURITY;
                ALTER TABLE notifications.notifications FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON notifications.notifications
                    USING ((SELECT tenancy.platform_scope_active()) OR agency_id = (SELECT tenancy.current_agency_id()))
                    WITH CHECK ((SELECT tenancy.platform_scope_active()) OR agency_id = (SELECT tenancy.current_agency_id()));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON notifications.notifications;");

            migrationBuilder.DropTable(
                name: "notification_templates",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "suppressed_email_addresses",
                schema: "notifications");
        }
    }
}
