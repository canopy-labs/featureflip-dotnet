using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Internal;

/// <summary>
/// Result of evaluating a feature flag.
/// </summary>
internal sealed class EvaluationResult
{
    public string VariationKey { get; }
    public EvaluationReason Reason { get; }
    public string? RuleId { get; }
    public string? PrerequisiteKey { get; }

    public EvaluationResult(string variationKey, EvaluationReason reason, string? ruleId = null, string? prerequisiteKey = null)
    {
        VariationKey = variationKey;
        Reason = reason;
        RuleId = ruleId;
        PrerequisiteKey = prerequisiteKey;
    }
}

/// <summary>
/// Evaluates feature flags against evaluation contexts.
/// </summary>
internal sealed class FlagEvaluator
{
    // Timeout for regex operations to prevent ReDoS attacks
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    // Safety net against pathological prerequisite chains. Cycles are blocked at
    // write time on the server, so reaching this limit indicates a corrupt config.
    internal const int MaxPrerequisiteDepth = 10;

#if NETSTANDARD2_0
    // Cache MD5 instance per thread to avoid allocation overhead
    private static readonly ThreadLocal<MD5> Md5Instance = new(() => MD5.Create());
#endif

    /// <summary>
    /// Evaluates a flag configuration against an evaluation context.
    /// </summary>
    /// <param name="flag">The flag to evaluate.</param>
    /// <param name="context">The evaluation context.</param>
    /// <param name="getSegment">Optional segment lookup for segment-keyed rules.</param>
    public EvaluationResult Evaluate(FlagConfiguration flag, EvaluationContext context, Func<string, Segment?>? getSegment = null)
    {
        return Evaluate(flag, context, allFlags: null, getSegment);
    }

    /// <summary>
    /// Evaluates a flag, resolving any prerequisite flags using <paramref name="allFlags"/>.
    /// </summary>
    /// <param name="flag">The flag to evaluate.</param>
    /// <param name="context">The evaluation context.</param>
    /// <param name="allFlags">
    /// Map of all flags in the environment, keyed by flag key. Required when the flag
    /// has prerequisites; pass <c>null</c> if the flag is known to have no prerequisites.
    /// </param>
    /// <param name="getSegment">Optional segment lookup for segment-keyed rules.</param>
    public EvaluationResult Evaluate(
        FlagConfiguration flag,
        EvaluationContext context,
        IReadOnlyDictionary<string, FlagConfiguration>? allFlags,
        Func<string, Segment?>? getSegment = null)
    {
        var memo = new Dictionary<string, EvaluationResult>(StringComparer.Ordinal);
        return EvaluateInternal(flag, context, allFlags, getSegment, depth: 0, memo);
    }

    /// <summary>
    /// Evaluates a flag, sharing a memoisation map with other concurrent evaluations
    /// (e.g. a batch "evaluate all" pass). Use this when evaluating multiple flags
    /// in one sweep so that shared prerequisite flags are only evaluated once.
    /// </summary>
    public EvaluationResult EvaluateWithSharedMemo(
        FlagConfiguration flag,
        EvaluationContext context,
        IReadOnlyDictionary<string, FlagConfiguration>? allFlags,
        Func<string, Segment?>? getSegment,
        Dictionary<string, EvaluationResult> memo)
    {
        return EvaluateInternal(flag, context, allFlags, getSegment, depth: 0, memo);
    }

    private EvaluationResult EvaluateInternal(
        FlagConfiguration flag,
        EvaluationContext context,
        IReadOnlyDictionary<string, FlagConfiguration>? allFlags,
        Func<string, Segment?>? getSegment,
        int depth,
        Dictionary<string, EvaluationResult> memo)
    {
        if (depth > MaxPrerequisiteDepth)
        {
            return new EvaluationResult(flag.OffVariation, EvaluationReason.Error);
        }

        // If flag is disabled, return off variation
        if (!flag.Enabled)
        {
            return new EvaluationResult(flag.OffVariation, EvaluationReason.FlagDisabled);
        }

        // Resolve prerequisites in order. A failing prerequisite short-circuits to the
        // off variation with reason PrerequisiteFailed; error reasons propagate upward.
        foreach (var prereq in flag.Prerequisites)
        {
            EvaluationResult prereqResult;
            if (memo.TryGetValue(prereq.PrerequisiteFlagKey, out var cached))
            {
                prereqResult = cached;
            }
            else if (allFlags == null || !allFlags.TryGetValue(prereq.PrerequisiteFlagKey, out var prereqFlag) || prereqFlag == null)
            {
                // Missing flag: fail safely. Write-time delete-blocking should normally
                // prevent this — treat as a prerequisite failure.
                var miss = new EvaluationResult(
                    flag.OffVariation,
                    EvaluationReason.PrerequisiteFailed,
                    ruleId: null,
                    prerequisiteKey: prereq.PrerequisiteFlagKey);
                memo[flag.Key] = miss;
                return miss;
            }
            else
            {
                prereqResult = EvaluateInternal(prereqFlag, context, allFlags, getSegment, depth + 1, memo);
                memo[prereq.PrerequisiteFlagKey] = prereqResult;
            }

            if (prereqResult.Reason == EvaluationReason.Error)
            {
                var err = new EvaluationResult(flag.OffVariation, EvaluationReason.Error);
                memo[flag.Key] = err;
                return err;
            }

            if (prereqResult.VariationKey != prereq.ExpectedVariationKey)
            {
                var failed = new EvaluationResult(
                    flag.OffVariation,
                    EvaluationReason.PrerequisiteFailed,
                    ruleId: null,
                    prerequisiteKey: prereq.PrerequisiteFlagKey);
                memo[flag.Key] = failed;
                return failed;
            }
        }

        // Evaluate rules in priority order (lower priority = higher precedence)
        var orderedRules = flag.Rules.OrderBy(r => r.Priority).ToList();

        foreach (var rule in orderedRules)
        {
            if (EvaluateRule(rule, context, getSegment))
            {
                var variationKey = ResolveServeConfig(rule.Serve!, context);
                var ruleResult = new EvaluationResult(variationKey, EvaluationReason.RuleMatch, rule.Id);
                memo[flag.Key] = ruleResult;
                return ruleResult;
            }
        }

        // No rules matched, use fallthrough
        if (flag.Fallthrough != null)
        {
            var variationKey = ResolveServeConfig(flag.Fallthrough, context);
            var fallResult = new EvaluationResult(variationKey, EvaluationReason.Fallthrough);
            memo[flag.Key] = fallResult;
            return fallResult;
        }

        // Default to off variation if no fallthrough
        var offResult = new EvaluationResult(flag.OffVariation, EvaluationReason.Fallthrough);
        memo[flag.Key] = offResult;
        return offResult;
    }

    private bool EvaluateRule(TargetingRule rule, EvaluationContext context, Func<string, Segment?>? getSegment)
    {
        // If rule references a segment, evaluate the segment's conditions instead
        if (!string.IsNullOrEmpty(rule.SegmentKey))
        {
            if (getSegment == null) return false;
            var segment = getSegment(rule.SegmentKey!);
            if (segment == null) return false;
            return EvaluateConditions(segment.Conditions, segment.ConditionLogic, context);
        }

        return EvaluateConditionGroups(rule.ConditionGroups, context);
    }

    private bool EvaluateConditionGroups(IReadOnlyList<ConditionGroup> groups, EvaluationContext context)
    {
        if (groups.Count == 0) return true;

        // All groups must match (AND between groups)
        return groups.All(group => EvaluateConditions(group.Conditions, group.Operator, context));
    }

    private bool EvaluateConditions(IReadOnlyList<Condition> conditions, ConditionLogic logic, EvaluationContext context)
    {
        if (conditions.Count == 0) return true;

        if (logic == ConditionLogic.And)
            return conditions.All(c => EvaluateCondition(c, context));
        else
            return conditions.Any(c => EvaluateCondition(c, context));
    }

    private bool EvaluateCondition(Condition condition, EvaluationContext context)
    {
        var attributeValue = context.GetAttribute(condition.Attribute);

        if (attributeValue == null)
        {
            return condition.Negate;
        }

        // Type-aware numeric coercion for equality operators (#1458). When the attribute is a
        // boxed CLR numeric (bool is excluded by omission) and the operator is an equality op,
        // compare numerically so that 1 == "1.0" and 1.0 == "1" — mirroring the engine. Each
        // literal is parsed with the same strict, culture-invariant parse as CompareNumeric, so
        // unparseable literals ("1abc") never match. String/bool attributes fall through to the
        // existing case-insensitive string path below.
        if (IsEqualityOperator(condition.Operator) && TryGetNumericValue(attributeValue, out var numericAttr))
        {
            var anyEqual = condition.Values.Any(v =>
                double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var nv) && nv == numericAttr);
            var numericResult = condition.Operator is ConditionOperator.Equals or ConditionOperator.In
                ? anyEqual
                : !anyEqual; // NotEquals / NotIn
            return condition.Negate ? !numericResult : numericResult;
        }

        var stringValue = ConvertToString(attributeValue);

        bool result = EvaluateOperator(condition.Operator, stringValue, condition.Values);

        return condition.Negate ? !result : result;
    }

    /// <summary>
    /// True for the four operators that coerce numerically when the attribute is a number:
    /// <see cref="ConditionOperator.Equals"/>, <see cref="ConditionOperator.NotEquals"/>,
    /// <see cref="ConditionOperator.In"/>, <see cref="ConditionOperator.NotIn"/>. Substring/
    /// prefix operators (Contains/StartsWith/EndsWith) are deliberately excluded.
    /// </summary>
    private static bool IsEqualityOperator(ConditionOperator op) =>
        op is ConditionOperator.Equals or ConditionOperator.NotEquals
            or ConditionOperator.In or ConditionOperator.NotIn;

    /// <summary>
    /// Extracts a <see cref="double"/> from a boxed CLR numeric attribute value, returning false
    /// for any non-numeric type. <c>bool</c> is deliberately NOT matched, so booleans keep the
    /// string-compare path. Context attributes are always supplied as native boxed primitives via
    /// <c>EvaluationContext.Set</c>, so no JSON-token handling is needed here.
    /// </summary>
    private static bool TryGetNumericValue(object value, out double result)
    {
        switch (value)
        {
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static string ConvertToString(object? value)
    {
        if (value == null) return string.Empty;

        // Use InvariantCulture for consistent string conversion across locales
        return value switch
        {
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private bool EvaluateOperator(ConditionOperator op, string value, List<string> conditionValues)
    {
        switch (op)
        {
            case ConditionOperator.Equals:
                return conditionValues.Any(v => string.Equals(value, v, StringComparison.OrdinalIgnoreCase));

            case ConditionOperator.NotEquals:
                return conditionValues.All(v => !string.Equals(value, v, StringComparison.OrdinalIgnoreCase));

            case ConditionOperator.Contains:
                return conditionValues.Any(v => value.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0);

            case ConditionOperator.NotContains:
                return conditionValues.All(v => value.IndexOf(v, StringComparison.OrdinalIgnoreCase) < 0);

            case ConditionOperator.StartsWith:
                return conditionValues.Any(v => value.StartsWith(v, StringComparison.OrdinalIgnoreCase));

            case ConditionOperator.EndsWith:
                return conditionValues.Any(v => value.EndsWith(v, StringComparison.OrdinalIgnoreCase));

            case ConditionOperator.In:
                return conditionValues.Any(v => string.Equals(value, v, StringComparison.OrdinalIgnoreCase));

            case ConditionOperator.NotIn:
                return conditionValues.All(v => !string.Equals(value, v, StringComparison.OrdinalIgnoreCase));

            case ConditionOperator.MatchesRegex:
                return conditionValues.Any(pattern =>
                {
                    try
                    {
                        // Case-sensitive matching mirrors the engine (RegexOptions.None).
                        // Case-insensitivity is opt-in via the (?i) inline flag in the pattern.
#if NETSTANDARD2_0
                        // netstandard2.0 doesn't support timeout in IsMatch overload
                        // Create a Regex instance with timeout instead
                        var regex = new Regex(pattern, RegexOptions.None, RegexTimeout);
                        return regex.IsMatch(value);
#else
                        return Regex.IsMatch(value, pattern, RegexOptions.None, RegexTimeout);
#endif
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // Pattern took too long to evaluate - treat as non-match for safety
                        return false;
                    }
                    catch
                    {
                        return false;
                    }
                });

            case ConditionOperator.GreaterThan:
                return CompareNumeric(value, conditionValues, (a, b) => a > b);

            case ConditionOperator.LessThan:
                return CompareNumeric(value, conditionValues, (a, b) => a < b);

            case ConditionOperator.GreaterThanOrEqual:
                return CompareNumeric(value, conditionValues, (a, b) => a >= b);

            case ConditionOperator.LessThanOrEqual:
                return CompareNumeric(value, conditionValues, (a, b) => a <= b);

            case ConditionOperator.Before:
                return CompareDateTime(value, conditionValues, (a, b) => a < b);

            case ConditionOperator.After:
                return CompareDateTime(value, conditionValues, (a, b) => a > b);

            case ConditionOperator.SemverEquals:
                return CompareSemver(value, conditionValues, c => c == 0);

            case ConditionOperator.SemverGreaterThan:
                return CompareSemver(value, conditionValues, c => c > 0);

            case ConditionOperator.SemverGreaterThanOrEqual:
                return CompareSemver(value, conditionValues, c => c >= 0);

            case ConditionOperator.SemverLessThan:
                return CompareSemver(value, conditionValues, c => c < 0);

            case ConditionOperator.SemverLessThanOrEqual:
                return CompareSemver(value, conditionValues, c => c <= 0);

            default:
                return false;
        }
    }

    /// <summary>
    /// Compares <paramref name="value"/> against each condition value as a semantic version,
    /// returning true when the comparison sign satisfies <paramref name="predicate"/> for any
    /// condition value. Unparseable versions contribute no match (consistent with the numeric and
    /// date/time operators). See <see cref="SemverComparer"/> for the precedence rules.
    /// </summary>
    private static bool CompareSemver(string value, List<string> conditionValues, Func<int, bool> predicate)
    {
        if (!SemverComparer.TryParse(value, out var left))
        {
            return false;
        }

        foreach (var conditionValue in conditionValues)
        {
            if (SemverComparer.TryParse(conditionValue, out var right) && predicate(SemverComparer.Compare(left, right)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when comparing <paramref name="value"/> as a number against any condition
    /// value satisfies <paramref name="comparison"/>. A non-numeric attribute value matches
    /// nothing, and non-numeric condition values are skipped — mirroring the engine's
    /// <c>double.TryParse</c> semantics. There is deliberately no lexical string-compare
    /// fallback: it produced matches the engine rejects (e.g. "gold" &gt; "abc"), #1456.
    /// </summary>
    private static bool CompareNumeric(string value, List<string> conditionValues, Func<double, double, bool> comparison)
    {
        // Use InvariantCulture for consistent numeric parsing across locales
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numValue))
        {
            return false;
        }

        foreach (var conditionValue in conditionValues)
        {
            if (double.TryParse(conditionValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var numCondition) &&
                comparison(numValue, numCondition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when comparing <paramref name="value"/> as a date/time against any condition
    /// value satisfies <paramref name="comparison"/>. Values are parsed to UTC-normalized
    /// <see cref="DateTimeOffset"/> (see <see cref="TryParseDateTime"/>), so timezone offsets are
    /// compared by instant rather than wall-clock. An unparseable attribute value matches nothing,
    /// and unparseable condition values are skipped — mirroring the engine's <c>CompareDateTime</c>.
    /// There is deliberately no lexical string-compare fallback: it produced matches the engine
    /// rejects (e.g. unparseable input wrongly matching), #1455.
    /// </summary>
    private static bool CompareDateTime(string value, List<string> conditionValues, Func<DateTimeOffset, DateTimeOffset, bool> comparison)
    {
        if (!TryParseDateTime(value, out var dateValue))
        {
            return false;
        }

        foreach (var conditionValue in conditionValues)
        {
            if (TryParseDateTime(conditionValue, out var conditionDate) && comparison(dateValue, conditionDate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses <paramref name="value"/> into a UTC-normalized <see cref="DateTimeOffset"/>. Accepts
    /// ISO-8601 timestamps (offset-bearing or offset-less, with offset-less assumed UTC) and a
    /// fallback of unix seconds since the epoch. Returns false when the value is neither — mirroring
    /// the engine's <c>TryParseDateTime</c>.
    /// </summary>
    private static bool TryParseDateTime(string value, out DateTimeOffset result)
    {
        // Use InvariantCulture for consistent date parsing across locales. AssumeUniversal treats
        // offset-less input as UTC; AdjustToUniversal normalizes offset-bearing input to UTC.
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
        {
            return true;
        }

        // Try unix timestamp (seconds since epoch)
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            try
            {
                result = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        return false;
    }

    private string ResolveServeConfig(ServeConfig serve, EvaluationContext context)
    {
        if (serve.Type == ServeType.Fixed)
        {
            return serve.Variation ?? string.Empty;
        }

        // Rollout
        var bucketBy = serve.BucketBy ?? "userId";
        var bucketValue = ConvertToString(context.GetAttribute(bucketBy));

        if (serve.Variations == null || serve.Variations.Count == 0)
        {
            return serve.Variation ?? string.Empty;
        }

        // Keyless user contexts can't be bucketed. Rather than hashing the empty value
        // into an arbitrary salt-dependent bucket, serve the control (first) variation
        // deterministically. The engine assigns a random GUID per eval (spreading
        // anonymous users over HTTP); local SDK eval is deterministic, so parity is
        // guaranteed only for keyed contexts (#1457).
        if (string.IsNullOrEmpty(bucketValue) && (bucketBy == "userId" || bucketBy == "user_id"))
        {
            return serve.Variations[0].Key;
        }

        var salt = serve.Salt ?? "";

        var bucket = CalculateBucket(salt, bucketValue);

        var cumulativeWeight = 0;
        foreach (var variation in serve.Variations)
        {
            cumulativeWeight += variation.Weight;
            if (bucket < cumulativeWeight)
            {
                return variation.Key;
            }
        }

        // Return last variation if bucket exceeds total weight
        return serve.Variations[serve.Variations.Count - 1].Key;
    }

    internal static int CalculateBucket(string salt, string value)
    {
        var input = $"{salt}:{value}";
        var inputBytes = Encoding.UTF8.GetBytes(input);

#if NETSTANDARD2_0
        var md5 = Md5Instance.Value!;
        var hashBytes = md5.ComputeHash(inputBytes);
        // Use little-endian for consistent hashing across architectures
        var hashValue = (uint)(hashBytes[0] | (hashBytes[1] << 8) | (hashBytes[2] << 16) | (hashBytes[3] << 24));
#else
        var hashBytes = MD5.HashData(inputBytes);
        // Use BinaryPrimitives for consistent little-endian reading across architectures
        var hashValue = BinaryPrimitives.ReadUInt32LittleEndian(hashBytes);
#endif
        return (int)(hashValue % 100);
    }
}
