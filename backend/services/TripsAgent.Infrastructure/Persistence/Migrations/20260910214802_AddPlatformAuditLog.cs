using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations;

/// <summary>
/// Creates <c>platform.audit_logs</c>: monthly range partitions, append-only, and the two
/// functions that keep the partition set moving.
///
/// Written by hand rather than scaffolded. EF Core has no vocabulary for declarative
/// partitioning, a trigger, or a REVOKE, so the table is created in SQL. The model snapshot
/// still describes the same columns, keys and indexes, which is what keeps
/// `has-pending-model-changes` honest.
/// </summary>
public partial class AddPlatformAuditLog : Migration
{
    // Hoisted out of the CreateIndex calls: an analyser rejects constant array arguments,
    // and naming them makes the index definitions easier to read anyway.
    private static readonly string[] ActorIndexColumns = ["actor_user_id", "occurred_at"];
    private static readonly string[] AgencyIndexColumns = ["agency_id", "occurred_at"];
    private static readonly string[] EntityIndexColumns = ["entity_type", "entity_id", "occurred_at"];
    private static readonly bool[] NewestFirst = [false, true];
    private static readonly bool[] NewestFirstAfterTwo = [false, false, true];
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "platform");

        // PARTITION BY RANGE on occurred_at. The primary key has to contain the partition
        // key — PostgreSQL cannot enforce uniqueness across partitions otherwise — hence
        // (id, occurred_at) rather than id alone.
        migrationBuilder.Sql(
            """
            CREATE TABLE platform.audit_logs (
                id               uuid                     NOT NULL,
                occurred_at      timestamp with time zone NOT NULL,
                agency_id        uuid                     NULL,
                actor_user_id    uuid                     NULL,
                actor_type       character varying(30)    NOT NULL,
                actor_ip_address character varying(100)   NULL,
                action           character varying(100)   NOT NULL,
                entity_type      character varying(200)   NOT NULL,
                entity_id        character varying(200)   NOT NULL,
                before_state     jsonb                    NULL,
                after_state      jsonb                    NULL,
                reason           character varying(1000)  NULL,
                correlation_id   character varying(100)   NULL,
                CONSTRAINT pk_audit_logs PRIMARY KEY (id, occurred_at)
            ) PARTITION BY RANGE (occurred_at);
            """);

        // Creates one month's partition, or returns quietly if it already exists, so the
        // maintenance job is safe to run as often as anyone likes.
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION platform.create_audit_log_partition(month date)
            RETURNS text
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                starts_on      date := date_trunc('month', month)::date;
                ends_on        date := (date_trunc('month', month) + interval '1 month')::date;
                partition_name text := 'audit_logs_' || to_char(starts_on, 'YYYY_MM');
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM pg_class c
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'platform' AND c.relname = partition_name
                ) THEN
                    RETURN partition_name;
                END IF;

                EXECUTE format(
                    'CREATE TABLE platform.%I PARTITION OF platform.audit_logs FOR VALUES FROM (%L) TO (%L)',
                    partition_name, starts_on, ends_on);

                RETURN partition_name;
            END;
            $function$;
            """);

        // Retention. Takes the window as an argument rather than hard-coding one, because
        // how long audit history must be kept is a legal question the project has not
        // settled yet (open question 26 in the delivery plan). The application passes its
        // configured value; changing it needs no migration.
        //
        // Dropping a whole partition is DDL, so it is not blocked by the append-only trigger
        // below — which is the point. Rows cannot be deleted; months can expire.
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION platform.drop_expired_audit_log_partitions(
                retain_months integer)
            RETURNS integer
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                cutoff    date := (date_trunc('month', now()) - make_interval(months => retain_months))::date;
                expired   record;
                dropped   integer := 0;
            BEGIN
                IF retain_months IS NULL OR retain_months < 1 THEN
                    RAISE EXCEPTION
                        'retain_months must be at least 1, got %', retain_months
                        USING HINT = 'Zero would drop the month still being written to.';
                END IF;

                FOR expired IN
                    SELECT child.relname AS name
                    FROM pg_inherits i
                    JOIN pg_class  child  ON child.oid  = i.inhrelid
                    JOIN pg_class  parent ON parent.oid = i.inhparent
                    JOIN pg_namespace pn  ON pn.oid     = parent.relnamespace
                    WHERE pn.nspname = 'platform'
                      AND parent.relname = 'audit_logs'
                      AND child.relname ~ '^audit_logs_[0-9]{4}_[0-9]{2}$'
                      AND to_date(right(child.relname, 7), 'YYYY_MM') < cutoff
                LOOP
                    EXECUTE format('DROP TABLE platform.%I', expired.name);
                    dropped := dropped + 1;
                END LOOP;

                RETURN dropped;
            END;
            $function$;
            """);

        // Append-only, enforced in the database rather than trusted to the application.
        // A row-level trigger on the partitioned parent is cloned onto every partition, so
        // it still fires when someone reaches past the parent and updates a partition
        // directly. A REVOKE alone would not: table owners keep their privileges regardless.
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION platform.reject_audit_log_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                RAISE EXCEPTION
                    'platform.audit_logs is append-only; % is not permitted', TG_OP
                    USING HINT = 'Write a new audit entry instead. History expires by whole partition only.';
            END;
            $function$;
            """);

        migrationBuilder.Sql(
            """
            CREATE TRIGGER audit_logs_append_only
            BEFORE UPDATE OR DELETE ON platform.audit_logs
            FOR EACH ROW
            EXECUTE FUNCTION platform.reject_audit_log_mutation();
            """);

        // The acceptance criterion asks for the privileges to be revoked from the
        // application role. That role is created when a database is provisioned, which has
        // not been built yet, so this applies the REVOKE if the conventional role is there
        // and stays quiet if it is not. The trigger above is what protects the table today.
        // See docs/runbooks/audit-log.md.
        migrationBuilder.Sql(
            """
            DO $do$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'tripsagent_app') THEN
                    REVOKE UPDATE, DELETE ON platform.audit_logs FROM tripsagent_app;
                END IF;
            END
            $do$;
            """);

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_actor",
            schema: "platform",
            table: "audit_logs",
            columns: ActorIndexColumns,
            descending: NewestFirst);

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_agency",
            schema: "platform",
            table: "audit_logs",
            columns: AgencyIndexColumns,
            descending: NewestFirst);

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_entity",
            schema: "platform",
            table: "audit_logs",
            columns: EntityIndexColumns,
            descending: NewestFirstAfterTwo);

        // Last month through three months ahead. Without a partition covering the current
        // instant every insert fails, so the table is never left with only the month it was
        // created in — a deploy on the 31st would otherwise break at midnight.
        migrationBuilder.Sql(
            """
            SELECT platform.create_audit_log_partition(
                (date_trunc('month', now()) + make_interval(months => n))::date)
            FROM generate_series(-1, 3) AS n;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Dropping the parent takes every partition with it.
        migrationBuilder.Sql("DROP TABLE IF EXISTS platform.audit_logs CASCADE;");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform.reject_audit_log_mutation();");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform.drop_expired_audit_log_partitions(integer);");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform.create_audit_log_partition(date);");
    }
}
