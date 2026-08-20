using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests;

/// <summary>
/// coreContractVectors — the shared CORE's contract, one layer above the evaluator.
/// </summary>
/// <remarks>
/// <see cref="GoldenVectorTests"/> asserts what the evaluator computes, with the
/// .NET engine as its oracle. This class asserts the shared core's client-facing
/// contract (typed-accessor strictness, malformed-variation handling), where the
/// engine has no opinion and in fact disagrees: it returns null where an SDK must
/// return the CALLER'S default. That is why #1989 and #2281 could not be locked
/// with the existing classes, and why both shipped as 6-of-7-SDK divergences that
/// no CI could see. These vectors are hand-authored, not engine-generated.
///
/// <c>expect.reason</c> is a CANONICAL token mapped to this SDK's vocabulary.
///
/// DO NOT edit vectors.json to make C# pass — if a vector fails, fix the SDK.
/// </remarks>
public class CoreContractTests
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(namingPolicy: null) }
    };

    private static readonly JsonElement Vectors = Load();

    private static JsonElement Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "golden", "vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    public static IEnumerable<object[]> Contracts() =>
        Vectors.GetProperty("coreContractVectors").EnumerateArray().Select(v => new object[] { v });

    private static EvaluationReason CanonicalReason(string token) => token switch
    {
        "Error" => EvaluationReason.Error,
        "Fallthrough" => EvaluationReason.Fallthrough,
        "FlagNotFound" => EvaluationReason.FlagNotFound,
        _ => throw new Xunit.Sdk.XunitException($"unmapped canonical reason '{token}'")
    };

    [Theory]
    [MemberData(nameof(Contracts))]
    public void CoreContract(JsonElement v)
    {
        var id = v.GetProperty("id").GetString()!;
        var description = v.GetProperty("description").GetString()!;

        var flags = v.GetProperty("flags").EnumerateArray()
            .Select(f => f.Deserialize<FlagConfiguration>(Wire)!)
            .ToList();

        var events = new List<EvaluationEvent>();
        var store = new FlagStore();
        store.Replace(flags, new List<Segment>());
        var options = new FeatureFlagOptions
        {
            Inspectors = new List<EvaluationInspector> { e => events.Add(e) }
        };
        using var client = new FeatureflipClient(store, new FlagEvaluator(), options);

        var flagKey = v.GetProperty("flagKey").GetString()!;
        var context = new EvaluationContext { UserId = v.GetProperty("context").GetProperty("userId").GetString() };
        var read = v.GetProperty("read");
        var expect = v.GetProperty("expect");
        var dflt = read.GetProperty("default");

        object? got = read.GetProperty("as").GetString() switch
        {
            "bool" => client.BoolVariation(flagKey, context, dflt.GetBoolean()),
            "string" => client.StringVariation(flagKey, context, dflt.GetString()!),
            "int" => client.IntVariation(flagKey, context, dflt.GetInt32()),
            "number" or "double" => client.DoubleVariation(flagKey, context, dflt.GetDouble()),
            var other => throw new Xunit.Sdk.XunitException($"unmapped read.as '{other}'")
        };

        var wanted = expect.GetProperty("value");
        switch (wanted.ValueKind)
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(wanted.GetBoolean(), (bool)got!);
                break;
            case JsonValueKind.String:
                Assert.Equal(wanted.GetString(), (string)got!);
                break;
            case JsonValueKind.Number:
                // Compare numerically so an int read and a double read of the same
                // JSON number agree.
                Assert.Equal(wanted.GetDouble(), Convert.ToDouble(got), 9);
                break;
            default:
                throw new Xunit.Sdk.XunitException($"[{id}] unsupported expected value kind {wanted.ValueKind}");
        }

        // Typed accessors return only a value, so the reason is observed through
        // the inspector — the same surface a real caller would use.
        var evt = Assert.Single(events);
        Assert.Equal(CanonicalReason(expect.GetProperty("reason").GetString()!), evt.Reason);
        Assert.False(string.IsNullOrEmpty(description));
    }

    /// <summary>A runner that silently skips everything is worse than no runner.</summary>
    [Fact]
    public void EveryContractVectorIsExercised()
    {
        var count = Vectors.GetProperty("coreContractVectors").GetArrayLength();
        Assert.True(count >= 14, $"only {count} contract vectors in the fixture");
    }
}
