using TripsAgent.Domain.Storefront;

namespace TripsAgent.Application.Storefront;

/// <summary>What came of a website builder request.</summary>
/// <typeparam name="T">What the caller gets back when it worked.</typeparam>
public abstract record StorefrontResult<T>
{
    private StorefrontResult()
    {
    }

    public sealed record Ok(T Value) : StorefrontResult<T>;

    /// <summary>Nothing by that id belongs to this agency. Another agency's row looks exactly like this.</summary>
    public sealed record NotFound(string Title) : StorefrontResult<T>;

    /// <summary>Fields that do not hold together, keyed the way the console's form fields are.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : StorefrontResult<T>;

    /// <summary>The request was fine, but the thing it acts on has moved on — someone else saved first.</summary>
    public sealed record Conflict(string Title, string Detail) : StorefrontResult<T>;

    /// <summary>A business rule said no — the publish gate, a sub-agent making a site. Every reason is listed.</summary>
    public sealed record Refused(string Title, string Detail, IReadOnlyList<PublishProblem> Problems) : StorefrontResult<T>;
}

/// <summary>Field-level errors, collected so one response can name every field that needs fixing.</summary>
public sealed class FieldErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool Any => _errors.Count > 0;

    public void Add(string field, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (!_errors.TryGetValue(field, out var messages))
        {
            messages = [];
            _errors[field] = messages;
        }

        messages.Add(message);
    }

    public IReadOnlyDictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
}
