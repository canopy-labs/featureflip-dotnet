using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests;

/// <summary>
/// Cross-SDK golden-vector parity harness (issue #1477).
/// Loads packages/csharp-sdk/tests/Featureflip.Client.Tests/golden/vectors.json (39 vectors)
/// and asserts that the C# SDK evaluator produces bit-for-bit identical results to the
/// engine reference implementation.
/// </summary>
public class GoldenVectorTests
{
    private static readonly JsonElement Vectors = LoadVectors();
    private static readonly FlagEvaluator Evaluator = new();

    // Matches Internal/FeatureFlagHttpClient.cs deserialization options.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(namingPolicy: null) }
    };

    private static JsonElement LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "golden", "vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    // ── Bucket vectors ─────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Buckets() =>
        Vectors.GetProperty("bucketVectors").EnumerateArray().Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(Buckets))]
    public void Bucket(JsonElement v)
    {
        var got = FlagEvaluator.CalculateBucket(
            v.GetProperty("salt").GetString()!,
            v.GetProperty("value").GetString()!);
        Assert.Equal(v.GetProperty("expectedBucket").GetInt32(), got);
    }

    // ── Rollout vectors ────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Rollouts() =>
        Vectors.GetProperty("rolloutVectors").EnumerateArray().Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(Rollouts))]
    public void Rollout(JsonElement v)
    {
        var weighted = v.GetProperty("variations").EnumerateArray()
            .Select(x => new WeightedVariation
            {
                Key = x.GetProperty("key").GetString()!,
                Weight = x.GetProperty("weight").GetInt32()
            })
            .ToList();

        var flag = new FlagConfiguration
        {
            Key = "rollout",
            Version = 1,
            Type = FlagType.String,
            Enabled = true,
            Variations = weighted.Select(w => new Variation
            {
                Key = w.Key,
                Value = JsonSerializer.SerializeToElement(w.Key)
            }).ToList(),
            Rules = new(),
            Fallthrough = new ServeConfig
            {
                Type = ServeType.Rollout,
                Salt = v.GetProperty("salt").GetString(),
                BucketBy = "userId",
                Variations = weighted
            },
            OffVariation = weighted[0].Key
        };

        var ctx = new EvaluationContext { UserId = v.GetProperty("value").GetString() };
        var result = Evaluator.Evaluate(flag, ctx, allFlags: null);
        Assert.Equal(v.GetProperty("expectedVariation").GetString(), result.VariationKey);
    }

    // ── Condition vectors ──────────────────────────────────────────────────────

    public static IEnumerable<object[]> Conditions() =>
        Vectors.GetProperty("conditionVectors").EnumerateArray().Select(v => new object[] { v });

    /// <summary>
    /// A date operand outside the ISO grammar must match nothing (#2480).
    /// <para>
    /// Hand-authored because the ENGINE DISSENTS. This SDK used to make the engine's identical
    /// <c>DateTimeOffset.TryParse</c> call and so inherited its leniency about the date FORMAT —
    /// it resolved <c>05/15/2023</c>, <c>Jan 1 2024</c> and <c>2024.01.01</c> — which meant a C#
    /// customer got a different targeting result than a Go one for the same saved config. The
    /// engine keeps that leniency because live customer rules depend on it; an SDK has no such
    /// constraint, so this one now pins the grammar and these vectors hold it there.
    /// </para>
    /// </summary>
    public static IEnumerable<object[]> DateGrammar() =>
        Vectors.GetProperty("dateGrammarVectors").EnumerateArray().Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(DateGrammar))]
    public void DateGrammarOp(JsonElement v) => ConditionOp(v);

    [Theory]
    [MemberData(nameof(Conditions))]
    public void ConditionOp(JsonElement v)
    {
        var op = Enum.Parse<ConditionOperator>(v.GetProperty("operator").GetString()!);
        var values = v.GetProperty("values").EnumerateArray().Select(x => x.GetString()!).ToList();
        var negate = v.TryGetProperty("negate", out var n) && n.GetBoolean();

        var flag = new FlagConfiguration
        {
            Key = "cond",
            Version = 1,
            Type = FlagType.String,
            Enabled = true,
            Variations = new()
            {
                new Variation { Key = "match",   Value = JsonSerializer.SerializeToElement("match") },
                new Variation { Key = "nomatch", Value = JsonSerializer.SerializeToElement("nomatch") }
            },
            Rules = new()
            {
                new TargetingRule
                {
                    Id = "r",
                    Priority = 0,
                    Serve = new ServeConfig { Type = ServeType.Fixed, Variation = "match" },
                    ConditionGroups = new()
                    {
                        new ConditionGroup
                        {
                            Operator = ConditionLogic.And,
                            Conditions = new()
                            {
                                new Condition
                                {
                                    Attribute = "attr",
                                    Operator = op,
                                    Values = values,
                                    Negate = negate
                                }
                            }
                        }
                    }
                }
            },
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = "nomatch" },
            OffVariation = "nomatch"
        };

        // Build a typed CLR attribute value so the #1458 numeric-coercion path is exercised
        // correctly. The C# SDK's TryGetNumericValue matches boxed CLR numerics (double, int, …)
        // but not JsonElement, so we pass native types — not SerializeToElement — here.
        var attrEl = v.GetProperty("attribute");
        object typedAttr = attrEl.GetProperty("type").GetString() switch
        {
            "number"  => attrEl.GetProperty("value").GetDouble(),
            "boolean" => (object)attrEl.GetProperty("value").GetBoolean(),
            _         => attrEl.GetProperty("value").GetString()!,
        };

        var ctx = new EvaluationContext().Set("attr", typedAttr);
        var result = Evaluator.Evaluate(flag, ctx, allFlags: null);
        Assert.Equal(v.GetProperty("expectedMatch").GetBoolean(), result.VariationKey == "match");
    }

    // ── Flag vectors ───────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Flags() =>
        Vectors.GetProperty("flagVectors").EnumerateArray().Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(Flags))]
    public void FullFlag(JsonElement v)
    {
        var flags = v.GetProperty("flags").EnumerateArray()
            .Select(f => f.Deserialize<FlagConfiguration>(Wire)!)
            .ToList();
        var segmentList = v.GetProperty("segments").EnumerateArray()
            .Select(s => s.Deserialize<Segment>(Wire)!)
            .ToList();
        var segmentDict = segmentList.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var allFlags = flags.ToDictionary(f => f.Key, StringComparer.Ordinal);

        var ctxEl = v.GetProperty("context");
        var ctx = new EvaluationContext
        {
            UserId = ctxEl.TryGetProperty("userId", out var u) ? u.GetString() : null
        };
        if (ctxEl.TryGetProperty("attributes", out var attrs))
        {
            foreach (var a in attrs.EnumerateObject())
            {
                // Convert to the native CLR type the SDK sees in production. The SDK's
                // numeric coercion (#1458) matches boxed CLR numerics, NOT JsonElement, so
                // passing a JsonElement would bypass coercion for a numeric attribute.
                // Mirror the condition-vector path above.
                object value = a.Value.ValueKind switch
                {
                    JsonValueKind.Number => a.Value.GetDouble(),
                    JsonValueKind.True or JsonValueKind.False => a.Value.GetBoolean(),
                    _ => a.Value.GetString()!,
                };
                ctx.Set(a.Name, value);
            }
        }

        var flagKey = v.GetProperty("flagKey").GetString()!;
        var flag = allFlags[flagKey];
        var result = Evaluator.Evaluate(
            flag,
            ctx,
            allFlags: allFlags,
            getSegment: k => segmentDict.GetValueOrDefault(k));

        var expected = v.GetProperty("expected");

        // Variation key
        Assert.Equal(expected.GetProperty("variation").GetString(), result.VariationKey);

        // Value — look up the variation's JsonElement value from the flag config. A served key
        // the flag doesn't define (reason Error, #4236) has no value: the vector expects null.
        var variation = flag.Variations.FirstOrDefault(var => var.Key == result.VariationKey);
        Assert.Equal(expected.GetProperty("value").GetRawText(),
            variation is null ? "null" : JsonSerializer.Serialize(variation.Value));

        // Reason
        var er = expected.GetProperty("reason");
        var expectedKind = er.GetProperty("kind").GetString()!;
        Assert.Equal(expectedKind, ReasonKind(result));

        if (er.TryGetProperty("ruleId", out var rid))
        {
            Assert.Equal(rid.GetString(), result.RuleId);
        }

        if (er.TryGetProperty("prerequisiteKey", out var prereqKey))
        {
            Assert.Equal(prereqKey.GetString(), result.PrerequisiteKey);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Normalises a C# SDK EvaluationReason to the PascalCase "kind" string used in the fixture.
    /// </summary>
    private static string ReasonKind(EvaluationResult result) => result.Reason.ToString();
    // EvaluationReason members: FlagDisabled, Fallthrough, RuleMatch, PrerequisiteFailed, Error
    // which map directly to the fixture's "kind" values.
}
