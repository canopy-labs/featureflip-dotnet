namespace Featureflip.Client;

/// <summary>
/// An in-process observer invoked on every flag evaluation. Register one or more via
/// <see cref="FeatureFlagOptions.Inspectors"/> to pipe exposures into your own
/// analytics/experimentation tooling.
/// </summary>
/// <remarks>
/// Inspectors run <em>synchronously</em> on the evaluating thread, inside the caller's
/// own <c>BoolVariation</c>/<c>StringVariation</c>/… call. Keep them cheap — hand work
/// off to a queue rather than blocking. Any exception thrown by an inspector is caught
/// and logged as a warning: it neither changes the value the caller receives nor
/// prevents the remaining inspectors from running.
/// <para>
/// Inspectors stop firing once the client is closed — more precisely, once the last
/// handle sharing its underlying client is disposed. Evaluating through a disposed
/// client still returns the same value it did before; only the notification is skipped.
/// </para>
/// </remarks>
/// <param name="evaluationEvent">The observed evaluation. Treat as read-only.</param>
public delegate void EvaluationInspector(EvaluationEvent evaluationEvent);
