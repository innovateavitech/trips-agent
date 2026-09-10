using Microsoft.Extensions.Logging;
using TripsAgent.Application.Tenancy;

namespace TripsAgent.Infrastructure.Tenancy;

/// <summary>
/// Logs every cross-tenant scope, then opens it. Scoped, so a scope cannot outlive its request.
/// </summary>
public sealed partial class PlatformScope : IPlatformScope
{
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<PlatformScope> _logger;

    private int _depth;

    public PlatformScope(ITenantContext tenantContext, ILogger<PlatformScope> logger)
    {
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public bool IsActive => _depth > 0;

    public IDisposable Enter(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A cross-tenant read must say why. The reason is written to the audit log, and "
                + "without it the entry is useless to whoever reads it later.",
                nameof(reason));
        }

        // Logged on entry rather than on exit: if the query throws, or the process dies
        // mid-request, we still want the record that the read was attempted.
        LogEntered(_logger, reason, _tenantContext.UserId, _tenantContext.AgencyId);

        _depth++;

        return new Handle(this);
    }

    private void Exit()
    {
        if (_depth > 0)
        {
            _depth--;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Cross-tenant read opened. Reason: {Reason}. User: {UserId}. Acting agency: {AgencyId}.")]
    private static partial void LogEntered(ILogger logger, string reason, Guid? userId, Guid? agencyId);

    /// <summary>
    /// Closes the scope on dispose. Nested scopes are counted rather than flattened, so an inner
    /// <c>using</c> block ending does not reopen the tenant filter while an outer one is still
    /// running.
    /// </summary>
    private sealed class Handle : IDisposable
    {
        private PlatformScope? _owner;

        public Handle(PlatformScope owner) => _owner = owner;

        public void Dispose()
        {
            _owner?.Exit();
            _owner = null;   // disposing twice must not decrement twice
        }
    }
}
