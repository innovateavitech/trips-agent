using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripsAgent.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Bounds <c>payments.release_sub_agent_allowance</c> by what the allowance actually holds, so no
    /// session can hand itself back more than was reserved. Issue 175, item 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The function is <c>SECURITY DEFINER</c> because a sub-agent's own request has to write a row its
    /// principal owns — see <c>AddSubAgentNetwork</c>, which installed it. Reserving was already
    /// bounded in the function: its UPDATE only matches while <c>spent_minor + amount &lt;= limit_minor</c>.
    /// Releasing was not. It took any amount and clamped the result at zero with <c>GREATEST</c>, so a
    /// release for more than was held succeeded quietly, and a double release — a reversal racing a
    /// lapse — wiped out what other bookings had reserved along with its own.
    /// </para>
    /// <para>
    /// Releasing is now conditional the same way: <c>WHERE spent_minor &gt;= p_amount</c>. Too large an
    /// amount matches no row, writes nothing and returns false. That fails in the safe direction — the
    /// cap goes on counting money that may already be back, which stops a booking rather than allowing
    /// one — and it still never raises, because every caller is on a failure path (the checkout
    /// sweeper, a reversal, a lapsed ticket time limit) where an exception would abort work that has to
    /// finish.
    /// </para>
    /// <para>
    /// <b>Replaced whole, security settings and all.</b> <c>CREATE OR REPLACE FUNCTION</c> rewrites the
    /// whole definition, so <c>SECURITY DEFINER</c> and <c>SET search_path</c> are restated here: leave
    /// either out and the function would quietly fall back to invoker rights, which a sub-agent's
    /// session does not have, and to a search path its caller could choose. Replacing a function keeps
    /// its privileges, but the grant is restated too so this migration reads on its own.
    /// <c>SubAgentAllowanceTests</c> checks all three against the function actually installed.
    /// </para>
    /// </remarks>
    public partial class BoundSubAgentAllowanceRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION payments.release_sub_agent_allowance(
                    p_sub_agency uuid,
                    p_currency   text,
                    p_amount     bigint)
                RETURNS boolean
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = payments, tenancy, public
                AS $fn$
                DECLARE
                    v_agency  uuid := tenancy.current_agency_id();
                    v_updated integer;
                BEGIN
                    IF p_amount IS NULL OR p_amount <= 0 THEN
                        RAISE EXCEPTION 'An allowance is released in a positive amount of kobo (got %).', p_amount
                            USING ERRCODE = 'invalid_parameter_value';
                    END IF;

                    -- Only the sub-agent itself, its own principal, or an audited platform scope.
                    IF NOT tenancy.platform_scope_active() THEN
                        IF v_agency IS NULL THEN
                            RETURN false;
                        END IF;

                        IF v_agency <> p_sub_agency
                           AND NOT EXISTS (SELECT 1 FROM tenancy.agencies
                                            WHERE id = p_sub_agency AND parent_agency_id = v_agency) THEN
                            RETURN false;
                        END IF;
                    END IF;

                    -- Bounded by what the allowance holds, as reserving is bounded by the limit
                    -- (issue 175). More than that matches no row, writes nothing and returns false.
                    -- GREATEST used to accept it and clamp at zero, which gave a second release of
                    -- the same hold whatever other bookings had reserved.
                    UPDATE payments.wallet_allowances
                       SET spent_minor = spent_minor - p_amount,
                           version     = version + 1,
                           updated_at  = now()
                     WHERE sub_agency_id = p_sub_agency
                       AND currency      = p_currency
                       AND spent_minor  >= p_amount;

                    GET DIAGNOSTICS v_updated = ROW_COUNT;
                    RETURN v_updated = 1;
                END;
                $fn$;
                """);

            migrationBuilder.Sql($"""
                GRANT EXECUTE ON FUNCTION payments.release_sub_agent_allowance(uuid, text, bigint)
                   TO {AddRowLevelSecurity.ApplicationRole};
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Exactly what AddSubAgentNetwork installed, security settings and grant included.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION payments.release_sub_agent_allowance(
                    p_sub_agency uuid,
                    p_currency   text,
                    p_amount     bigint)
                RETURNS boolean
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = payments, tenancy, public
                AS $fn$
                DECLARE
                    v_agency  uuid := tenancy.current_agency_id();
                    v_updated integer;
                BEGIN
                    IF p_amount IS NULL OR p_amount <= 0 THEN
                        RAISE EXCEPTION 'An allowance is released in a positive amount of kobo (got %).', p_amount
                            USING ERRCODE = 'invalid_parameter_value';
                    END IF;

                    IF NOT tenancy.platform_scope_active() THEN
                        IF v_agency IS NULL THEN
                            RETURN false;
                        END IF;

                        IF v_agency <> p_sub_agency
                           AND NOT EXISTS (SELECT 1 FROM tenancy.agencies
                                            WHERE id = p_sub_agency AND parent_agency_id = v_agency) THEN
                            RETURN false;
                        END IF;
                    END IF;

                    -- GREATEST, not a bare subtraction: a double release — a reversal racing a
                    -- lapse — must leave the allowance at zero spent, not below it.
                    UPDATE payments.wallet_allowances
                       SET spent_minor = GREATEST(spent_minor - p_amount, 0),
                           version     = version + 1,
                           updated_at  = now()
                     WHERE sub_agency_id = p_sub_agency
                       AND currency      = p_currency;

                    GET DIAGNOSTICS v_updated = ROW_COUNT;
                    RETURN v_updated = 1;
                END;
                $fn$;
                """);

            migrationBuilder.Sql($"""
                GRANT EXECUTE ON FUNCTION payments.release_sub_agent_allowance(uuid, text, bigint)
                   TO {AddRowLevelSecurity.ApplicationRole};
                """);
        }
    }
}
