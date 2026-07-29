namespace Featureflip.Client;

/// <summary>
/// Represents the context for evaluating feature flags, containing user attributes.
/// </summary>
public sealed class EvaluationContext
{
    private readonly Dictionary<string, object> _attributes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The unique identifier for the user. Used for percentage rollouts.</summary>
    public string? UserId { get; set; }

    /// <summary>The user's email address.</summary>
    public string? Email { get; set; }

    /// <summary>The user's country code.</summary>
    public string? Country { get; set; }

    /// <summary>
    /// Sets a custom attribute on the context.
    /// </summary>
    public EvaluationContext Set(string key, object value)
    {
        _attributes[key] = value;
        return this;
    }

    /// <summary>
    /// Gets an attribute value by key. Built-in UserId takes precedence, then custom attributes, then other built-ins.
    /// </summary>
    public object? GetAttribute(string key)
    {
        var lower = key.ToLowerInvariant();

        // Built-in user_id takes precedence over custom attributes
        if (lower is "userid" or "user_id" && UserId is not null)
            return UserId;

        // Custom attributes next
        if (_attributes.TryGetValue(key, out var value))
            return value;

        // Other built-in properties last
        return lower switch
        {
            "email" => Email,
            "country" => Country,
            _ => null
        };
    }

    /// <summary>
    /// Returns a shallow copy of this context: the built-in fields plus a fresh
    /// attribute dictionary holding the same attribute references. Used to hand
    /// evaluation inspectors a context they cannot use to mutate the caller's object.
    /// </summary>
    internal EvaluationContext Copy()
    {
        var copy = new EvaluationContext
        {
            UserId = UserId,
            Email = Email,
            Country = Country
        };

        foreach (var attribute in _attributes)
        {
            copy._attributes[attribute.Key] = attribute.Value;
        }

        return copy;
    }
}
