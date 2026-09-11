using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Tells PostgreSQL which agency a connection is acting for, so row-level security enforces tenant
/// isolation even where an EF query filter is missing. See ADR-0006.
/// </summary>
/// <remarks>
/// <para>
/// Two settings per connection, read by the policies through <c>tenancy.current_agency_id()</c> and
/// <c>tenancy.platform_scope_active()</c>: <c>app.agency_id</c>, the resolved tenant or empty; and
/// <c>app.platform_scope</c>, <c>on</c> while <see cref="IPlatformScope"/> is active.
/// </para>
/// <para>
/// <b>Session-level and re-applied, not transaction-local.</b> The obvious version is
/// <c>set_config(..., is_local =&gt; true)</c>, and here it would silently do nothing: EF runs most
/// commands in autocommit, so a transaction-local setting sent as its own statement expires with
/// that statement, before the query it was meant for. So the settings are written at session level
/// every time EF opens the connection — which also means a pooled connection handed to the next
/// request is rewritten before that request's first command.
/// </para>
/// <para>
/// <b>Re-checked before every command.</b> The tenant is fixed per request, but the platform scope
/// opens and closes within one. If it changes while EF holds a connection open, the settings are
/// rewritten before the next command rather than left describing a moment that has passed. Most
/// commands find nothing to do.
/// </para>
/// <para>
/// <b>Settings made inside a transaction are provisional.</b> A session-level setting made inside a
/// transaction is undone if it rolls back. So the transaction is remembered alongside the settings,
/// and once a command runs outside it — committed or rolled back, we cannot tell which — they are
/// written again.
/// </para>
/// <para>
/// This is a backstop against a missed filter, not a defence against SQL injection: code that can
/// run arbitrary SQL can set these too. ADR-0006 is explicit about what it does and does not buy.
/// </para>
/// </remarks>
public sealed class TenantSessionInterceptor : IDbConnectionInterceptor, IDbCommandInterceptor
{
    private const string ApplySql =
        "SELECT set_config('app.agency_id', @agency_id, false), "
        + "set_config('app.platform_scope', @platform_scope, false)";

    private readonly ITenantContext _tenantContext;
    private readonly IPlatformScope _platformScope;

    // What each connection was last told. Weak, so a connection EF throws away is not kept alive.
    private readonly ConditionalWeakTable<DbConnection, Applied> _applied = new();

    public TenantSessionInterceptor(ITenantContext tenantContext, IPlatformScope platformScope)
    {
        _tenantContext = tenantContext;
        _platformScope = platformScope;
    }

    // ------------------------------------------------------------------ connections

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        Apply(connection, transaction: null);

    public Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(connection, transaction: null, cancellationToken);

    // ------------------------------------------------------------------ commands

    public InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        EnsureCurrent(command);
        return result;
    }

    public async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentAsync(command, cancellationToken);
        return result;
    }

    public InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        EnsureCurrent(command);
        return result;
    }

    public async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentAsync(command, cancellationToken);
        return result;
    }

    public InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        EnsureCurrent(command);
        return result;
    }

    public async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentAsync(command, cancellationToken);
        return result;
    }

    // ------------------------------------------------------------------ the work

    private void EnsureCurrent(DbCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Connection is { } connection && !IsCurrent(connection, command.Transaction))
        {
            Apply(connection, command.Transaction);
        }
    }

    private async Task EnsureCurrentAsync(DbCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Connection is { } connection && !IsCurrent(connection, command.Transaction))
        {
            await ApplyAsync(connection, command.Transaction, cancellationToken);
        }
    }

    private bool IsCurrent(DbConnection connection, DbTransaction? transaction) =>
        _applied.TryGetValue(connection, out var applied)
        && applied.AgencyId == _tenantContext.AgencyId
        && applied.PlatformScope == _platformScope.IsActive

        // Written inside a transaction that has since ended: it may have been rolled back.
        && (applied.Transaction is null || ReferenceEquals(applied.Transaction, transaction));

    private void Apply(DbConnection connection, DbTransaction? transaction)
    {
        using var command = CreateApplyCommand(connection, transaction, out var applied);
        command.ExecuteNonQuery();
        _applied.AddOrUpdate(connection, applied);
    }

    private async Task ApplyAsync(DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateApplyCommand(connection, transaction, out var applied);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _applied.AddOrUpdate(connection, applied);
    }

    private DbCommand CreateApplyCommand(DbConnection connection, DbTransaction? transaction, out Applied applied)
    {
        applied = new Applied(_tenantContext.AgencyId, _platformScope.IsActive, transaction);

        // Created on the connection directly, so EF never sees it and it cannot recurse here.
        var command = connection.CreateCommand();
        command.CommandText = ApplySql;
        command.Transaction = transaction;

        var agency = command.CreateParameter();
        agency.ParameterName = "agency_id";
        agency.DbType = DbType.String;
        agency.Value = applied.AgencyId?.ToString() ?? string.Empty;
        command.Parameters.Add(agency);

        var scope = command.CreateParameter();
        scope.ParameterName = "platform_scope";
        scope.DbType = DbType.String;
        scope.Value = applied.PlatformScope ? "on" : "off";
        command.Parameters.Add(scope);

        return command;
    }

    private sealed record Applied(Guid? AgencyId, bool PlatformScope, DbTransaction? Transaction);
}
