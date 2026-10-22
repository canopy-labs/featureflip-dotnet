using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    private readonly ILogger _logger;

    public FlagEvaluator() : this(NullLogger.Instance)
    {
    }

    internal FlagEvaluator(ILogger logger)
    {
        _logger = logger ?? NullLogger.Instance;
    }

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
            return OffResult(flag, EvaluationReason.Error);
        }

        // If flag is disabled, return off variation
        if (!flag.Enabled)
        {
            return OffResult(flag, EvaluationReason.FlagDisabled);
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
                var miss = OffResult(flag, EvaluationReason.PrerequisiteFailed, prereq.PrerequisiteFlagKey);
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
                var err = OffResult(flag, EvaluationReason.Error);
                memo[flag.Key] = err;
                return err;
            }

            if (prereqResult.VariationKey != prereq.ExpectedVariationKey)
            {
                var failed = OffResult(flag, EvaluationReason.PrerequisiteFailed, prereq.PrerequisiteFlagKey);
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
                var variationKey = ResolveServeConfig(rule.Serve!, context, flag.Key, rule.Id);
                if (variationKey == null)
                {
                    // Can't bucket this context: the rule doesn't match, try the next one.
                    continue;
                }

                if (!HasVariation(flag, variationKey))
                {
                    // The served key names no variation: malformed config. Report Error (no
                    // rule id) with the served key, like the engine's ServeVariation (#4236).
                    var missing = new EvaluationResult(variationKey, EvaluationReason.Error);
                    memo[flag.Key] = missing;
                    return missing;
                }

                var ruleResult = new EvaluationResult(variationKey, EvaluationReason.RuleMatch, rule.Id);
                memo[flag.Key] = ruleResult;
                return ruleResult;
            }
        }

        // No rules matched, use fallthrough
        if (flag.Fallthrough != null)
        {
            // A fallthrough that can't bucket serves its Variation (the same degrade as a
            // rollout without variations) with the normal fallthrough reason.
            var variationKey = ResolveServeConfig(flag.Fallthrough, context, flag.Key, null)
                ?? flag.Fallthrough.Variation ?? string.Empty;
            if (!HasVariation(flag, variationKey))
            {
                var missing = new EvaluationResult(variationKey, EvaluationReason.Error);
                memo[flag.Key] = missing;
                return missing;
            }

            var fallResult = new EvaluationResult(variationKey, EvaluationReason.Fallthrough);
            memo[flag.Key] = fallResult;
            return fallResult;
        }

        // Default to off variation if no fallthrough
        var offResult = new EvaluationResult(flag.OffVariation, EvaluationReason.Fallthrough);
        memo[flag.Key] = offResult;
        return offResult;
    }

    /// <summary>
    /// Serves the flag's off variation. An off variation the flag doesn't define is malformed
    /// config: Error with the key, like the engine's ServeVariation (#4236).
    /// </summary>
    private static EvaluationResult OffResult(FlagConfiguration flag, EvaluationReason reason, string? prerequisiteKey = null)
    {
        if (!HasVariation(flag, flag.OffVariation))
        {
            return new EvaluationResult(flag.OffVariation, EvaluationReason.Error);
        }

        return new EvaluationResult(flag.OffVariation, reason, ruleId: null, prerequisiteKey: prerequisiteKey);
    }

    private static bool HasVariation(FlagConfiguration flag, string key)
    {
        foreach (var v in flag.Variations)
        {
            if (v.Key == key) return true;
        }

        return false;
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

        bool? result = EvaluateOperator(condition.Operator, stringValue, condition.Values);

        // Issue #2262: an unrecognised operator fails CLOSED. Inverting it with
        // Negate would turn "I cannot evaluate this" into "matches every user",
        // serving the flag to 100% of traffic. Every ConditionOperator member is
        // covered by EvaluateOperator, and FeatureFlagHttpClient's
        // JsonStringEnumConverter rejects an unknown operator before it reaches
        // here, so this is defensive — it holds the invariant if a member is
        // added without a matching case, and keeps this SDK's contract identical
        // to the string-typed SDKs (js/go/ruby/php) where the input IS reachable.
        if (result is null)
        {
            return false;
        }

        return condition.Negate ? !result.Value : result.Value;
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

    /// <summary>
    /// Bucket-path spelling: floating types use the canonical number spelling shared with the
    /// Evaluation API (#4236); booleans are JSON's "true"/"false" (not bool.ToString()'s
    /// "True"/"False"); everything else (integers, strings) is unchanged.
    /// Conditions keep <see cref="ConvertToString"/>.
    /// </summary>
    private static string ConvertToBucketString(object? value) => value switch
    {
        bool b => b ? "true" : "false",
        System.Text.Json.JsonElement je => FormatJsonElementForBucket(je),
        double d => CanonicalNumber.Format(d),
        float f => CanonicalNumber.Format(f),
        // An integral decimal keeps its exact integer digits (like the engine's raw integer
        // text); anything else goes through double.
        decimal m => m == decimal.Truncate(m)
            ? (m == 0m ? "0" : decimal.Truncate(m).ToString("0", System.Globalization.CultureInfo.InvariantCulture))
            : CanonicalNumber.Format((double)m),
        _ => ConvertToString(value)
    };

    /// <summary>Mirrors the Evaluation API's BucketValueFormatter for JSON-element attributes.</summary>
    private static string FormatJsonElementForBucket(System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.True: return "true";
            case System.Text.Json.JsonValueKind.False: return "false";
            case System.Text.Json.JsonValueKind.Null:
            case System.Text.Json.JsonValueKind.Undefined:
                return string.Empty; // missing: the rollout can't bucket
            case System.Text.Json.JsonValueKind.Number:
                var raw = element.GetRawText();
                var start = raw.Length > 0 && raw[0] == '-' ? 1 : 0;
                var integral = start < raw.Length;
                for (var i = start; i < raw.Length; i++)
                {
                    if (raw[i] < '0' || raw[i] > '9') { integral = false; break; }
                }
                if (integral)
                {
                    // Negative zero is zero in every SDK's JSON parser, which spells it "0".
                    return start == 1 && raw.Substring(1).Trim('0').Length == 0 ? "0" : raw;
                }
                return CanonicalNumber.Format(element.GetDouble());
            default:
                return element.ToString();
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

    /// <summary>
    /// Applies a single operator to <paramref name="value"/>. Returns <c>null</c> — not
    /// <c>false</c> — for an operator this SDK does not recognise, so callers can tell
    /// "cannot evaluate" apart from "evaluated, did not match"; only the latter may be
    /// inverted by <c>Negate</c> (#2262).
    /// </summary>
    private bool? EvaluateOperator(ConditionOperator op, string value, List<string> conditionValues)
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
                // Unrecognised operator — "cannot evaluate", not "did not match".
                return null;
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
    /// The ONLY characters trimmed from a date operand, and the whole of the operand's
    /// permitted whitespace: U+0009..U+000D plus U+0020 — exactly the class
    /// <c>NumberStyles.Integer</c> accepts via <c>AllowLeadingWhite | AllowTrailingWhite</c>.
    /// </summary>
    private static readonly char[] DateOperandWhitespace = ['\t', '\n', '\v', '\f', '\r', ' '];

    /// <summary>
    /// True when <paramref name="s"/> carries a character no date operand may contain: a NUL or
    /// other control character, a non-ASCII whitespace character, or a zero-width no-break space.
    /// An interior ASCII space is allowed — it is the ISO-8601 date/time separator.
    /// <para>
    /// Mirrors the engine's guard of the same name. Without it the two parse branches below
    /// disagree with each other — <c>long.TryParse</c> silently accepts a TRAILING NUL as a
    /// string terminator while <c>DateTimeOffset.TryParse</c> accepts a leading U+00A0 the
    /// number branch rejects — and neither accident is expressible in a hand-rolled SDK parser
    /// without special-casing it (#2468).
    /// </para>
    /// </summary>
    private static bool HasForbiddenOperandChar(string s)
    {
        foreach (var c in s)
        {
            if (char.IsControl(c) || c == '\uFEFF' || (c != ' ' && char.IsWhiteSpace(c)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The ISO-8601 grammar a date operand may use: a calendar date, optionally followed by a time
    /// (seconds and fractional seconds optional) and an optional offset in either extended
    /// (<c>+05:00</c> / <c>Z</c>) or basic (<c>+0500</c>) form. The separator may be <c>T</c> or a
    /// space — the engine accepts both.
    /// <para>
    /// Byte-for-byte the grammar go, java, python, ruby, php and js pin. <c>[0-9]</c> and not
    /// <c>\d</c>, which in .NET matches every Unicode decimal digit (#2467).
    /// </para>
    /// </summary>
    private static readonly Regex IsoOperand = new(
        @"^([0-9]{4}-[0-9]{2}-[0-9]{2})(?:[T ]([0-9]{2}):([0-9]{2})(?::([0-9]{2}))?(\.[0-9]+)?(Z|[+-][0-9]{2}:?[0-9]{2})?)?$",
        RegexOptions.None);

    /// <summary>
    /// Rewrites an accepted ISO operand into the strict extended form — <c>T</c> separator, seconds
    /// present, offset spelled <c>+HH:MM</c> or <c>Z</c> — or returns null when it is not an
    /// accepted ISO shape.
    /// <para>
    /// Gating alone would not be enough, which is why every SDK canonicalizes instead: this SDK
    /// still hands the result to <c>DateTimeOffset.TryParse</c>, and that parser REJECTS two shapes
    /// the grammar admits and the other six SDKs resolve — a decimal on the minutes position
    /// (<c>2024-01-31T09:30.5</c>) and a basic offset (<c>+0500</c>). Rewriting them into the form
    /// .NET accepts is what makes the seven agree rather than merely narrows this one.
    /// </para>
    /// </summary>
    private static string? CanonicalizeIso(string s)
    {
        var m = IsoOperand.Match(s);
        if (!m.Success)
        {
            return null;
        }

        var date = m.Groups[1].Value;
        if (!m.Groups[2].Success)
        {
            return date;
        }

        // The engine rejects hour 24 outright rather than rolling it over. An ordinal compare
        // covers 25..99 too; the group is always exactly two ASCII digits.
        var hh = m.Groups[2].Value;
        if (string.CompareOrdinal(hh, "24") >= 0)
        {
            return null;
        }

        var mm = m.Groups[3].Value;
        var ss = m.Groups[4].Success ? m.Groups[4].Value : "00";
        var frac = m.Groups[5].Success ? m.Groups[5].Value : string.Empty;
        var off = m.Groups[6].Success ? m.Groups[6].Value : string.Empty;
        if (off.Length == 5)
        {
            // Basic offset (+0500) -> extended (+05:00). Substring rather than a range so this
            // still compiles for the netstandard2.0 target.
            off = off.Substring(0, 3) + ":" + off.Substring(3);
        }

        return date + "T" + hh + ":" + mm + ":" + ss + frac + off;
    }

    /// <summary>
    /// Parses <paramref name="value"/> into a UTC-normalized <see cref="DateTimeOffset"/>. Accepts
    /// an ISO-8601 timestamp matching <see cref="IsoOperand"/> (offset-bearing or offset-less, with
    /// offset-less assumed UTC) and a fallback of unix seconds since the epoch. Returns false when
    /// the value is neither.
    /// <para>
    /// This DELIBERATELY no longer mirrors the engine's <c>TryParseDateTime</c> (#2480). The engine
    /// hands the raw operand to <c>DateTimeOffset.TryParse</c>, which is lenient about the date
    /// FORMAT rather than merely about ISO-8601 — it resolves <c>05/15/2023</c>, <c>Jan 1 2024</c>
    /// and <c>2024.01.01</c> — and this SDK inherited that by making the identical call, so a C#
    /// customer got a different targeting result than a Go one for the same saved config. The
    /// engine keeps its leniency because live customer rules depend on it; an SDK has no such
    /// constraint, so this one joins the other six instead. Management rejects these operands on
    /// write, so the gap cannot widen.
    /// </para>
    /// </summary>
    private static bool TryParseDateTime(string value, out DateTimeOffset result)
    {
        result = default;

        // Normalize at the front door so both branches below see the same string, and so the
        // accepted whitespace class is this method's own stated contract rather than whatever
        // each branch's parser happens to tolerate (#2468).
        var s = value.Trim(DateOperandWhitespace);
        if (s.Length == 0 || HasForbiddenOperandChar(s))
        {
            return false;
        }

        // Use InvariantCulture for consistent date parsing across locales. AssumeUniversal treats
        // offset-less input as UTC; AdjustToUniversal normalizes offset-bearing input to UTC. The
        // CANONICALIZED string is parsed, never the raw operand — that is the whole guard.
        var canonical = CanonicalizeIso(s);
        if (canonical != null && DateTimeOffset.TryParse(canonical, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
        {
            return true;
        }

        // Try unix timestamp (seconds since epoch)
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
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

    /// <summary>
    /// Resolves a serve to a variation key, or <c>null</c> when a rollout can't bucket the
    /// context (no value for its bucket-by attribute). Fixed serves never return null.
    /// </summary>
    private string? ResolveServeConfig(ServeConfig serve, EvaluationContext context, string flagKey, string? ruleId)
    {
        if (serve.Type == ServeType.Fixed)
        {
            return serve.Variation ?? string.Empty;
        }

        // Rollout
        var bucketBy = serve.BucketBy ?? "userId";
        var bucketValue = ConvertToBucketString(context.GetAttribute(bucketBy));

        if (serve.Variations == null || serve.Variations.Count == 0)
        {
            return serve.Variation ?? string.Empty;
        }

        // A rollout with no value for its bucket-by attribute can't be placed in a bucket.
        // Skip it rather than serving an arbitrary variation (#4236). Whitespace, "0" and
        // false are real values and bucket normally.
        if (string.IsNullOrEmpty(bucketValue))
        {
            UnbucketableWarnings.Warn(_logger, flagKey, ruleId, bucketBy);
            return null;
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
