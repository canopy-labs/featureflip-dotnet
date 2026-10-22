using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Featureflip.Client.Internal;

/// <summary>
/// Logs, once per (flag key, rule id or "fallthrough", bucketBy) per process, that a
/// percentage rollout was skipped because the context had no value to bucket on.
/// Never logs context values. Capped so a hostile flag set can't grow it without bound.
/// </summary>
internal sealed class UnbucketableWarnings
{
    internal const int MaxKeys = 1000;

    private static readonly UnbucketableWarnings Shared = new();

    private readonly ConcurrentDictionary<string, byte> _seen = new();
    private int _count;

    /// <summary>Process-wide dedup, the one the evaluator uses.</summary>
    internal static void Warn(ILogger logger, string flagKey, string? ruleId, string bucketBy)
        => Shared.Record(logger, flagKey, ruleId, bucketBy);

    // Instance form so tests can exercise the cap without exhausting the shared set.
    internal void Record(ILogger logger, string flagKey, string? ruleId, string bucketBy)
    {
        var where = ruleId == null ? "fallthrough" : "rule " + ruleId;
        var key = flagKey + "\u0000" + where + "\u0000" + bucketBy;

        if (_seen.ContainsKey(key)) return;

        // Reserve a slot first so concurrent first-callers can't exceed the cap.
        if (Interlocked.Increment(ref _count) > MaxKeys)
        {
            Interlocked.Decrement(ref _count);
            return;
        }

        if (!_seen.TryAdd(key, 0))
        {
            // Lost a race with another thread adding the same key; it logs.
            Interlocked.Decrement(ref _count);
            return;
        }

        logger.LogWarning(
            "[featureflip] Skipped the percentage rollout in flag \"{FlagKey}\" ({Where}): the context has no value for \"{BucketBy}\". Pass a stable id in that attribute to include this context in the rollout.",
            flagKey, where, bucketBy);
    }
}
