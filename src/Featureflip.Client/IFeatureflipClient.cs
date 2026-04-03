namespace Featureflip.Client;

/// <summary>
/// Interface for evaluating feature flags.
/// </summary>
public interface IFeatureflipClient : IDisposable
{
    /// <summary>
    /// Evaluates a flag and returns the value.
    /// </summary>
    T Variation<T>(string key, EvaluationContext context, T defaultValue);

    /// <summary>
    /// Evaluates a flag and returns detailed information about the evaluation.
    /// </summary>
    EvaluationDetail<T> VariationDetail<T>(string key, EvaluationContext context, T defaultValue);

    /// <summary>Evaluates a boolean flag.</summary>
    bool BoolVariation(string key, EvaluationContext context, bool defaultValue);

    /// <summary>Evaluates a string flag.</summary>
    string StringVariation(string key, EvaluationContext context, string defaultValue);

    /// <summary>Evaluates an integer flag.</summary>
    int IntVariation(string key, EvaluationContext context, int defaultValue);

    /// <summary>Evaluates a double flag.</summary>
    double DoubleVariation(string key, EvaluationContext context, double defaultValue);

    /// <summary>Evaluates a JSON flag and deserializes to the specified type.</summary>
    T JsonVariation<T>(string key, EvaluationContext context, T defaultValue);

    /// <summary>Forces pending evaluation events to be sent to the server synchronously.</summary>
    /// <remarks>
    /// Prefer using <see cref="FlushAsync"/> in async contexts to avoid blocking threads.
    /// </remarks>
    void Flush();

    /// <summary>Forces pending evaluation events to be sent to the server asynchronously.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the flush operation.</returns>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Indicates whether the client has successfully initialized with flag data.</summary>
    bool IsInitialized { get; }
}
