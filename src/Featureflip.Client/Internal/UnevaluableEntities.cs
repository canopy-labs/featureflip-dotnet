using System.Collections.Generic;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging;

namespace Featureflip.Client.Internal;

/// <summary>
/// Entity-level drop for enum values this SDK build cannot evaluate (#2402).
/// </summary>
/// <remarks>
/// <para>
/// Companion to <see cref="TolerantEvaluatedEnumConverter{TEnum}"/>, which turns an
/// unrecognised <see cref="ServeType"/> or <see cref="ConditionLogic"/> name into a
/// sentinel rather than throwing. This is the half that acts on it: the flag or segment
/// carrying the sentinel is dropped, and the rest of the payload applies.
/// </para>
/// <para>
/// That middle course is the whole point. Storing the value would let it reach a two-way
/// dispatch and take the ELSE arm — a silent mis-evaluation. Rejecting the payload would
/// let one additive server change take down every flag on a pinned client. Dropping the
/// entity routes the caller through the FlagNotFound path the SDK already implements, so
/// they get their default for exactly the affected flag and nothing else changes.
/// </para>
/// <para>
/// Dropping a SEGMENT leaves rules pointing at it dangling, which is safe:
/// <see cref="FlagEvaluator"/> already returns false for an unresolvable
/// <c>SegmentKey</c>, so the cascade fails CLOSED. The engine-generated
/// <c>f-segment-unresolvable</c> golden vector pins that.
/// </para>
/// </remarks>
internal static class UnevaluableEntities
{
    /// <summary>
    /// A copy of <paramref name="response"/> carrying only the entities this build can
    /// evaluate, logging one line per drop.
    /// </summary>
    public static GetFlagsResponse DropUnevaluable(GetFlagsResponse response, ILogger? logger = null)
    {
        var flags = new List<FlagConfiguration>(response.Flags.Count);
        foreach (var flag in response.Flags)
        {
            var reason = FlagReason(flag);
            if (reason != null)
            {
                logger?.LogWarning(
                    "Dropping flag {FlagKey}: {Reason}. This SDK version may be older than the "
                    + "flag configuration; the rest of the configuration was applied.",
                    flag.Key, reason);
                continue;
            }
            flags.Add(flag);
        }

        var segments = new List<Segment>(response.Segments.Count);
        foreach (var segment in response.Segments)
        {
            var reason = SegmentReason(segment);
            if (reason != null)
            {
                logger?.LogWarning(
                    "Dropping segment {SegmentKey}: {Reason}. This SDK version may be older than "
                    + "the flag configuration; the rest of the configuration was applied.",
                    segment.Key, reason);
                continue;
            }
            segments.Add(segment);
        }

        return response with { Flags = flags, Segments = segments };
    }

    /// <summary>
    /// Why this flag cannot be evaluated, or <c>null</c> if it can. A reason rather than a
    /// bool so the diagnostic can name the FIELD that carried the bad value; the value
    /// itself is gone by this point, since the sentinel does not carry it.
    /// </summary>
    public static string? FlagReason(FlagConfiguration flag)
    {
        var fallthrough = ServeReason(flag.Fallthrough, "fallthrough");
        if (fallthrough != null) return fallthrough;

        foreach (var rule in flag.Rules)
        {
            var serve = ServeReason(rule.Serve, $"rule[{rule.Id}].serve");
            if (serve != null) return serve;

            foreach (var group in rule.ConditionGroups)
            {
                if (group.Operator == TolerantConditionLogicConverter.Unrecognised)
                {
                    return $"rule[{rule.Id}].conditionGroup.operator is not a condition logic "
                        + "this SDK version understands";
                }
            }
        }

        return null;
    }

    /// <summary>Why this segment cannot be evaluated, or <c>null</c> if it can.</summary>
    public static string? SegmentReason(Segment segment) =>
        segment.ConditionLogic == TolerantConditionLogicConverter.Unrecognised
            ? "conditionLogic is not a condition logic this SDK version understands"
            : null;

    private static string? ServeReason(ServeConfig? serve, string path) =>
        serve != null && serve.Type == TolerantServeTypeConverter.Unrecognised
            ? $"{path}.type is not a serve type this SDK version understands"
            : null;
}
