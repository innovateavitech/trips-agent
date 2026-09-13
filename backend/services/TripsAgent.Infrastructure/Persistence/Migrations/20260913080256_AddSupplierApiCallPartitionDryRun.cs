using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// A dry run for the supplier call log's partition job (issue 105).
    /// </summary>
    /// <remarks>
    /// Dropping a partition destroys a month of call history in one statement, and the job runs
    /// unattended at 03:15. This splits "which months are expired" out of the function that drops them,
    /// so the job can report exactly what it would drop without dropping it — and so the dry run and the
    /// live run can never disagree about the list, because they read it from the same place.
    /// </remarks>
    public partial class AddSupplierApiCallPartitionDryRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(ExpiredPartitions);
            migrationBuilder.Sql(DropUsingExpiredPartitions);

            // The application role only reads through the parent table and never names a partition; the
            // listing function is the maintenance job's, which runs as the schema owner.
            migrationBuilder.Sql(
                "REVOKE ALL ON FUNCTION supplier.expired_supplier_api_call_partitions(integer) FROM PUBLIC;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(DropWithItsOwnLoop);
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS supplier.expired_supplier_api_call_partitions(integer);");
        }

        /// <summary>The months that are past retention, newest last. Reads; drops nothing.</summary>
        private const string ExpiredPartitions = """
            CREATE OR REPLACE FUNCTION supplier.expired_supplier_api_call_partitions(
                retain_months integer)
            RETURNS SETOF text
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                cutoff date := (date_trunc('month', now()) - make_interval(months => retain_months))::date;
            BEGIN
                IF retain_months IS NULL OR retain_months < 1 THEN
                    RAISE EXCEPTION
                        'retain_months must be at least 1, got %', retain_months
                        USING HINT = 'Zero would drop the month still being written to.';
                END IF;

                RETURN QUERY
                    SELECT child.relname::text
                    FROM pg_inherits i
                    JOIN pg_class  child  ON child.oid  = i.inhrelid
                    JOIN pg_class  parent ON parent.oid = i.inhparent
                    JOIN pg_namespace pn  ON pn.oid     = parent.relnamespace
                    WHERE pn.nspname = 'supplier'
                      AND parent.relname = 'supplier_api_calls'
                      AND child.relname ~ '^supplier_api_calls_[0-9]{4}_[0-9]{2}$'
                      AND to_date(right(child.relname, 7), 'YYYY_MM') < cutoff
                    ORDER BY child.relname;
            END;
            $function$;
            """;

        /// <summary>Drops exactly what the listing function names, so the two can never disagree.</summary>
        private const string DropUsingExpiredPartitions = """
            CREATE OR REPLACE FUNCTION supplier.drop_expired_supplier_api_call_partitions(
                retain_months integer)
            RETURNS integer
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                expired text;
                dropped integer := 0;
            BEGIN
                FOR expired IN
                    SELECT * FROM supplier.expired_supplier_api_call_partitions(retain_months)
                LOOP
                    EXECUTE format('DROP TABLE supplier.%I', expired);
                    dropped := dropped + 1;
                END LOOP;

                RETURN dropped;
            END;
            $function$;
            """;

        /// <summary>The function as AddSupplierSchema left it, for Down.</summary>
        private const string DropWithItsOwnLoop = """
            CREATE OR REPLACE FUNCTION supplier.drop_expired_supplier_api_call_partitions(
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
                    WHERE pn.nspname = 'supplier'
                      AND parent.relname = 'supplier_api_calls'
                      AND child.relname ~ '^supplier_api_calls_[0-9]{4}_[0-9]{2}$'
                      AND to_date(right(child.relname, 7), 'YYYY_MM') < cutoff
                LOOP
                    EXECUTE format('DROP TABLE supplier.%I', expired.name);
                    dropped := dropped + 1;
                END LOOP;

                RETURN dropped;
            END;
            $function$;
            """;
    }
}
