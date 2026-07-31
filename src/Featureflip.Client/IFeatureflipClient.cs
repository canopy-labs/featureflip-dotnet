namespace Featureflip.Client;

/// <summary>
/// Interface for evaluating feature flags.
/// </summary>
public interface IFeatureflipClient : IDisposable
{
    /// <summary>
    /// Raised when the configuration of one or more flags changes, carrying the affected
    /// flag keys.
    /// </summary>
    /// <remarks>
    /// Batched — one raise per update carrying every affected key. Never raised with an
    /// empty collection, never for the initial flag load (use
    /// <see cref="WaitForInitializationAsync"/> for that), and never after the client is
    /// disposed — though subscribing via <c>+=</c> after disposal is itself a silent
    /// no-op rather than an <see cref="ObjectDisposedException"/>. A throwing handler is
    /// isolated: it neither stops the remaining handlers nor disturbs the client's
    /// polling/streaming loop, and the throw is logged at warning. Registering the same
    /// handler delegate twice via <c>+=</c> is a no-op — unlike standard
    /// multicast-delegate semantics, the handler still runs at most once per update, and
    /// the first <c>-=</c> unregisters it. When multiple client handles share an
    /// underlying connection, each handle's subscriptions are independent: disposing one
    /// handle never revokes another handle's registered handlers. The handler is invoked
    /// synchronously on an SDK background thread, never on the caller's
    /// <see cref="System.Threading.SynchronizationContext"/> — a UI consumer
    /// (WPF/WinForms) must marshal back to its own thread itself, or risk an
    /// <see cref="InvalidOperationException"/> from touching UI-owned objects off-thread.
    /// Keep handlers fast: delivery to a handle's handlers runs inline on that thread, so
    /// a slow handler delays subsequent flag delivery and, on the SSE path, can stall long
    /// enough to trip the idle-connection watchdog into a reconnect.
    /// </remarks>
    event EventHandler<FlagsChangedEventArgs> FlagsChanged;

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

    /// <summary>
    /// Returns a task that completes once the client's initial flag load has
    /// finished (or fallen back to offline). Completes immediately if already
    /// initialized. Honors <paramref name="cancellationToken"/> only while the
    /// load is still pending.
    /// </summary>
    Task WaitForInitializationAsync(CancellationToken cancellationToken = default);
}
