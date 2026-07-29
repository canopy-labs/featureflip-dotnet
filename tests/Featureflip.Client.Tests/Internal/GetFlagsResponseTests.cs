using System.Text.Json;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

public class GetFlagsResponseTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Deserialize_WithNullFlagsAndSegments_YieldsEmptyCollections()
    {
        // System.Text.Json invokes the init accessor with null for an explicit
        // `"flags": null`, overriding the property initializer — so the collections
        // must normalize null to empty rather than handing callers a null list.
        var json = """{"environment":"prod","version":3,"flags":null,"segments":null}""";

        var response = JsonSerializer.Deserialize<GetFlagsResponse>(json, JsonOptions);

        Assert.NotNull(response);
        Assert.NotNull(response!.Flags);
        Assert.NotNull(response.Segments);
        Assert.Empty(response.Flags);
        Assert.Empty(response.Segments);
    }

    [Fact]
    public void Deserialize_WithNullFlags_CountIsSafeToRead()
    {
        // Regression: LoadFlagsAsync logs `response.Flags.Count` straight after a
        // null-safe Replace. A null list threw there, and because the throw landed
        // after the store update but before the initialization signal, the client
        // never completed initialization and re-failed on every poll.
        var json = """{"flags":null,"segments":null}""";

        var response = JsonSerializer.Deserialize<GetFlagsResponse>(json, JsonOptions);

        // Reading .Count is the exact operation that threw.
        var flagCount = response!.Flags.Count;
        var segmentCount = response.Segments.Count;

        Assert.Equal(0, flagCount);
        Assert.Equal(0, segmentCount);
    }

    [Fact]
    public void Deserialize_WithOmittedFlags_YieldsEmptyCollections()
    {
        var response = JsonSerializer.Deserialize<GetFlagsResponse>("""{"version":1}""", JsonOptions);

        Assert.Empty(response!.Flags);
        Assert.Empty(response.Segments);
    }

    [Fact]
    public void Deserialize_WithPopulatedFlags_PreservesThem()
    {
        var json = """
        {"flags":[{"key":"flag-a","enabled":true}],"segments":[{"key":"seg-a"}]}
        """;

        var response = JsonSerializer.Deserialize<GetFlagsResponse>(json, JsonOptions);

        Assert.Single(response!.Flags);
        Assert.Equal("flag-a", response.Flags[0].Key);
        Assert.Single(response.Segments);
    }

    [Fact]
    public void ObjectInitializer_WithNullFlags_NormalizesToEmpty()
    {
        var response = new GetFlagsResponse { Flags = null!, Segments = null! };

        Assert.Empty(response.Flags);
        Assert.Empty(response.Segments);
    }
}
