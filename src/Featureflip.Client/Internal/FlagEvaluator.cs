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

    public EvaluationResult(string variationKey, EvaluationReason reason, string? ruleId = null)
    {
        VariationKey = variationKey;
        Reason = reason;
        RuleId = ruleId;
    }
}

/// <summary>
/// Evaluates feature flags against evaluation contexts.
/// </summary>
internal sealed class FlagEvaluator
{
    // Timeout for regex operations to prevent ReDoS attacks
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

#if NETSTANDARD2_0
    // Cache MD5 instance per thread to avoid allocation overhead
    private static readonly ThreadLocal<MD5> Md5Instance = new(() => MD5.Create());
#endif

    /// <summary>
    /// Evaluates a flag configuration against an evaluation context.
    /// </summary>
    public EvaluationResult Evaluate(FlagConfiguration flag, EvaluationContext context, Func<string, Segment?>? getSegment = null)
    {
        // If flag is disabled, return off variation
        if (!flag.Enabled)
        {
            return new EvaluationResult(flag.OffVariation, EvaluationReason.FlagDisabled);
        }

        // Evaluate rules in priority order (lower priority = higher precedence)
        var orderedRules = flag.Rules.OrderBy(r => r.Priority).ToList();

        foreach (var rule in orderedRules)
        {
            if (EvaluateRule(rule, context, getSegment))
            {
                var variationKey = ResolveServeConfig(rule.Serve!, context, flag.Key);
                return new EvaluationResult(variationKey, EvaluationReason.RuleMatch, rule.Id);
            }
        }

        // No rules matched, use fallthrough
        if (flag.Fallthrough != null)
        {
            var variationKey = ResolveServeConfig(flag.Fallthrough, context, flag.Key);
            return new EvaluationResult(variationKey, EvaluationReason.Fallthrough);
        }

        // Default to off variation if no fallthrough
        return new EvaluationResult(flag.OffVariation, EvaluationReason.Fallthrough);
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

        var stringValue = ConvertToString(attributeValue);

        bool result = EvaluateOperator(condition.Operator, stringValue, condition.Values);

        return condition.Negate ? !result : result;
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
#if NETSTANDARD2_0
                        // netstandard2.0 doesn't support timeout in IsMatch overload
                        // Create a Regex instance with timeout instead
                        var regex = new Regex(pattern, RegexOptions.IgnoreCase, RegexTimeout);
                        return regex.IsMatch(value);
#else
                        return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase, RegexTimeout);
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
                return conditionValues.Any(v => CompareNumeric(value, v) > 0);

            case ConditionOperator.LessThan:
                return conditionValues.Any(v => CompareNumeric(value, v) < 0);

            case ConditionOperator.GreaterThanOrEqual:
                return conditionValues.Any(v => CompareNumeric(value, v) >= 0);

            case ConditionOperator.LessThanOrEqual:
                return conditionValues.Any(v => CompareNumeric(value, v) <= 0);

            case ConditionOperator.Before:
                return conditionValues.Any(v => CompareDateTime(value, v) < 0);

            case ConditionOperator.After:
                return conditionValues.Any(v => CompareDateTime(value, v) > 0);

            default:
                return false;
        }
    }

    private static int CompareNumeric(string value, string conditionValue)
    {
        // Use InvariantCulture for consistent numeric parsing across locales
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numValue) &&
            double.TryParse(conditionValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var numCondition))
        {
            return numValue.CompareTo(numCondition);
        }
        // If not numeric, fall back to string comparison
        return string.Compare(value, conditionValue, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareDateTime(string value, string conditionValue)
    {
        // Use InvariantCulture for consistent date parsing across locales
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateValue) &&
            DateTime.TryParse(conditionValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateCondition))
        {
            return dateValue.CompareTo(dateCondition);
        }
        // If not valid dates, fall back to string comparison
        return string.Compare(value, conditionValue, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveServeConfig(ServeConfig serve, EvaluationContext context, string flagKey)
    {
        if (serve.Type == ServeType.Fixed)
        {
            return serve.Variation ?? string.Empty;
        }

        // Rollout
        var bucketBy = serve.BucketBy ?? "userId";
        var bucketValue = ConvertToString(context.GetAttribute(bucketBy));
        var salt = serve.Salt ?? flagKey;

        var bucket = CalculateBucket(salt, bucketValue);

        if (serve.Variations == null || serve.Variations.Count == 0)
        {
            return serve.Variation ?? string.Empty;
        }

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

    private static int CalculateBucket(string salt, string value)
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
