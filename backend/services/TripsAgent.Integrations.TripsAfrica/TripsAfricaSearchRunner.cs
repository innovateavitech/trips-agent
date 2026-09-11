using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripsAgent.Application.Persistence;
using TripsAgent.Application.Suppliers;
using TripsAgent.Domain.Suppliers;

namespace TripsAgent.Integrations.TripsAfrica;

/// <summary>
/// Stops searching a supplier that keeps failing, for a while, so an outage costs agents one quick
/// "try again" instead of forty seconds of spinner per search.
/// </summary>
/// <remarks>
/// A plain circuit breaker, one per product. After <see cref="TripsAfricaOptions.CircuitBreakerThreshold"/>
/// consecutive failed searches it opens and refuses searches for the cooldown; then it lets searches
/// through again, and the first failure re-opens it at once rather than after another full count.
/// A singleton, because the failures that matter are everyone's, not one request's.
/// </remarks>
public sealed class TripsAfricaSearchCircuits
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SupplierProductType, State> _states = [];
    private readonly TripsAfricaOptions _options;
    private readonly TimeProvider _clock;

    public TripsAfricaSearchCircuits(TripsAfricaOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    /// <summary>False while the circuit is open, with how long until it lets a search through.</summary>
    public bool TryEnter(SupplierProductType product, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var state = StateFor(product);
            var now = _clock.GetUtcNow();

            if (state.OpenUntil is { } until && now < until)
            {
                retryAfter = until - now;
                return false;
            }

            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public void RecordSuccess(SupplierProductType product)
    {
        lock (_gate)
        {
            var state = StateFor(product);
            state.Failures = 0;
            state.OpenUntil = null;
        }
    }

    public void RecordFailure(SupplierProductType product)
    {
        lock (_gate)
        {
            var state = StateFor(product);
            state.Failures++;

            // OpenUntil still set means this was the trial search after a cooldown: one failure is enough.
            if (state.OpenUntil is not null || state.Failures >= _options.CircuitBreakerThreshold)
            {
                state.OpenUntil = _clock.GetUtcNow() + _options.CircuitBreakerCooldown;
            }
        }
    }

    private State StateFor(SupplierProductType product)
    {
        if (!_states.TryGetValue(product, out var state))
        {
            state = new State();
            _states[product] = state;
        }

        return state;
    }

    private sealed class State
    {
        public int Failures { get; set; }

        public DateTimeOffset? OpenUntil { get; set; }
    }
}

/// <summary>
/// Which merchant account a call uses: the encrypted store first, the configured fallback second.
/// </summary>
public sealed class TripsAfricaCredentials
{
    private readonly ISupplierCredentialStore _store;
    private readonly TripsAfricaOptions _options;

    public TripsAfricaCredentials(ISupplierCredentialStore store, TripsAfricaOptions options)
    {
        _store = store;
        _options = options;
    }

    public async Task<SupplierCredentials> ForAsync(SupplierCallContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stored = await _store.FindAsync(TripsAfricaOptions.SupplierCode, _options.Environment, context.AgencyId, cancellationToken);

        if (stored is not null)
        {
            return stored;
        }

        if (_options.HasFallbackCredential)
        {
            return new SupplierCredentials(
                CredentialId: Guid.Empty,
                TripsAfricaOptions.SupplierCode,
                _options.Environment,
                AgencyId: null,
                _options.MerchantCode!,
                _options.MerchantKey!,
                BearerToken: string.IsNullOrWhiteSpace(_options.BearerToken) || _options.BearerToken == "REPLACE_ME"
                    ? null
                    : _options.BearerToken);
        }

        throw new InvalidOperationException(
            $"There is no Trips Africa {_options.Environment} credential. Store one in supplier_credentials, "
            + "or set TripsAfrica__MerchantCode and TripsAfrica__MerchantKey in .env.");
    }
}

/// <summary>The <c>suppliers</c> row both adapters audit their calls against.</summary>
public sealed class TripsAfricaSupplier
{
    private readonly IAppDbContext _db;
    private Guid? _id;

    public TripsAfricaSupplier(IAppDbContext db) => _db = db;

    /// <summary>For tests: an id already known, so no database is needed to look it up.</summary>
    internal TripsAfricaSupplier(Guid knownId)
    {
        _db = null!;
        _id = knownId;
    }

    public async Task<Guid> IdAsync(CancellationToken cancellationToken = default)
    {
        if (_id is { } known)
        {
            return known;
        }

        _id = await _db.Suppliers
                  .Where(supplier => supplier.Code == TripsAfricaOptions.SupplierCode)
                  .Select(supplier => (Guid?)supplier.Id)
                  .SingleOrDefaultAsync(cancellationToken)
              ?? throw new InvalidOperationException(
                  $"No '{TripsAfricaOptions.SupplierCode}' row in supplier.suppliers. Run `migrate`: the reference "
                  + "data seeder adds it.");

        return _id.Value;
    }
}

/// <summary>
/// Runs one search: through the circuit breaker, retried when the supplier did not answer, and
/// never when it answered no.
/// </summary>
/// <remarks>
/// <b>The only retry in this integration, and it is for search alone.</b> Search is a read, so a
/// second attempt after a timeout costs nothing but time. The same loop around a booking call would
/// issue a second ticket (ADR-0003), which is why this class takes a search path and nothing else
/// calls it.
/// </remarks>
public sealed partial class TripsAfricaSearchRunner
{
    private readonly TripsAfricaSearchHttp _http;
    private readonly TripsAfricaCredentials _credentials;
    private readonly TripsAfricaSupplier _supplier;
    private readonly TripsAfricaSearchCircuits _circuits;
    private readonly TripsAfricaOptions _options;
    private readonly ILogger<TripsAfricaSearchRunner> _logger;

    public TripsAfricaSearchRunner(
        TripsAfricaSearchHttp http,
        TripsAfricaCredentials credentials,
        TripsAfricaSupplier supplier,
        TripsAfricaSearchCircuits circuits,
        TripsAfricaOptions options,
        ILogger<TripsAfricaSearchRunner> logger)
    {
        _http = http;
        _credentials = credentials;
        _supplier = supplier;
        _circuits = circuits;
        _options = options;
        _logger = logger;
    }

    public async Task<TripsAfricaResponse> SearchAsync(
        string path,
        string body,
        SupplierProductType product,
        SupplierCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_circuits.TryEnter(product, out var retryAfter))
        {
            throw new SupplierUnavailableException(
                $"Trips Africa {product} search is paused for {Math.Ceiling(retryAfter.TotalSeconds):0} more seconds "
                + "after repeated failures. Nothing was sent.");
        }

        var credentials = await _credentials.ForAsync(context, cancellationToken);
        var supplierId = await _supplier.IdAsync(cancellationToken);
        var attempts = Math.Max(1, _options.SearchAttempts);
        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var response = await _http.PostAsync(
                    path, body, product, credentials, supplierId, SupplierOperation.Search, context, cancellationToken);

                if (response.IsSuccess)
                {
                    _circuits.RecordSuccess(product);
                    return response;
                }

                if (response.IsClientError)
                {
                    // It answered, so it is up — and asking again gets the same no.
                    _circuits.RecordSuccess(product);
                    throw new SupplierRequestRejectedException(
                        (int)response.StatusCode,
                        $"Trips Africa refused the {product} search with HTTP {(int)response.StatusCode}.");
                }

                lastFailure = new HttpRequestException(
                    $"Trips Africa answered the {product} search with HTTP {(int)response.StatusCode}.");
            }
            catch (SupplierCallOutcomeUnknownException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // For a booking call this would be an unknown outcome to poll. For a search it is
                // just a search that took too long, and asking again is harmless.
                lastFailure = ex;
            }
            catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = ex;
            }

            LogAttemptFailed(_logger, product, attempt, attempts, lastFailure);
        }

        _circuits.RecordFailure(product);

        throw new SupplierUnavailableException(
            $"Trips Africa did not answer the {product} search after {attempts} attempts. Nothing was bought.",
            lastFailure!);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trips Africa {Product} search attempt {Attempt} of {Attempts} failed")]
    private static partial void LogAttemptFailed(
        ILogger logger, SupplierProductType product, int attempt, int attempts, Exception? exception);
}
