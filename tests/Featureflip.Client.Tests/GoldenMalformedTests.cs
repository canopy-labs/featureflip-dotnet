using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;
using Xunit.Sdk;

namespace Featureflip.Client.Tests;

/// <summary>
/// Runner for the shared <c>malformedConfigVectors</c> class (#2315).
///
/// <para>The rule: a config payload violating the wire contract is discarded WHOLESALE,
/// never partially applied. One server bug (#2279 — enums as integers over SSE but strings
/// over REST) produced five different user-visible outcomes because every SDK improvised;
/// this class makes a sixth improvisation fail a build instead of shipping.</para>
///
/// <para>C# was one of the SDKs that failed this, and did so most quietly: both option sets
/// left <c>allowIntegerValues</c> at its <c>true</c> default, so an integer resolved to an
/// enum member by ORDINAL. Fixed in #2315; <c>IntegerEnumRejectionTests</c> pins it per-SDK
/// and this runner pins the same ground from the shared fixture.</para>
///
/// <para>Deliberately drives the <b>shipped</b> option sets rather than a locally-built
/// replica — a replica keeps passing when the real configuration regresses, which is exactly
/// how the original defect survived.</para>
/// </summary>
public class GoldenMalformedTests
{
    private static readonly JsonElement Block = LoadBlock();

    private static JsonElement LoadBlock()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "golden", "vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("malformedConfigVectors").Clone();
    }

    /// Both transports parse the same payload shape, and letting them diverge is its own
    /// bug class (#2279) — so every vector runs against both.
    public static IEnumerable<object[]> Vectors() =>
        from transport in new[] { "polling", "streaming" }
        from v in Block.GetProperty("vectors").EnumerateArray()
        select new object[] { transport, v.GetProperty("id").GetString()!, v };

    private static JsonSerializerOptions Options(string transport)
    {
        if (transport == "streaming") return SseStreamReader.SnapshotJsonOptions;
        using var client = new FeatureFlagHttpClient(
            "test-sdk-key", new FeatureFlagOptions { BaseUrl = "http://localhost:1" });
        return client.JsonOptions;
    }

    /// <summary>
    /// Applies the shared seed. A runner whose seed silently failed would "pass" every
    /// reject vector for entirely the wrong reason.
    /// </summary>
    private static FlagStore SeededStore(JsonSerializerOptions options)
    {
        var seed = JsonSerializer.Deserialize<GetFlagsResponse>(
            Block.GetProperty("seed").GetRawText(), options);
        Assert.NotNull(seed);

        var store = new FlagStore();
        store.Replace(seed!.Flags, seed.Segments);
        Assert.True(store.TryGetFlag("mc-seed", out _), "seed snapshot did not apply");
        return store;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void MalformedConfigVector(string transport, string id, JsonElement v)
    {
        var options = Options(transport);
        var store = SeededStore(options);
        var description = v.GetProperty("description").GetString();
        var label = $"[{id}/{transport}] {description}";

        bool applied;
        try
        {
            var parsed = JsonSerializer.Deserialize<GetFlagsResponse>(
                v.GetProperty("payload").GetRawText(), options);
            store.Replace(parsed!.Flags, parsed.Segments);
            applied = true;
        }
        catch (JsonException)
        {
            applied = false;
        }

        switch (v.GetProperty("expect").GetString())
        {
            case "reject":
                Assert.False(applied, $"{label}: payload was accepted");
                // Wholesale: the previous config still serves, nothing leaked in.
                Assert.True(store.TryGetFlag("mc-seed", out _), $"{label}: seeded config was replaced");
                Assert.False(store.TryGetFlag("mc-bad-type", out _), $"{label}: partially applied");
                break;
            case "accept":
                Assert.True(applied, $"{label}: forward-compatible payload was rejected");
                Assert.True(store.TryGetFlag("mc-accepted-flag", out _), $"{label}: did not apply");
                break;
            default:
                throw new XunitException($"unmapped expect for {id}");
        }
    }

    [Fact]
    public void EveryVectorIsExercised()
    {
        var count = Block.GetProperty("vectors").GetArrayLength();
        Assert.True(count >= 8, $"runner must not silently execute nothing; got {count}");
    }
}
