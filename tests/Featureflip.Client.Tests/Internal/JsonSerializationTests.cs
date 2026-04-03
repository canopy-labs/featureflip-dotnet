using System.Text.Json;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

public class JsonSerializationTests
{
    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(namingPolicy: null) }
    };

    [Fact]
    public void Deserialize_PascalCaseEnums_FromApiResponse()
    {
        // The API sends PascalCase enum values — the SDK must deserialize them correctly
        var json = """
        {
            "flagType": "Boolean",
            "serveType": "Fixed"
        }
        """;

        var options = CreateOptions();
        var result = JsonSerializer.Deserialize<EnumTestDto>(json, options);

        Assert.NotNull(result);
        Assert.Equal(FlagType.Boolean, result!.FlagType);
        Assert.Equal(ServeType.Fixed, result.ServeType);
    }

    [Fact]
    public void Deserialize_PascalCaseConditionOperator_FromApiResponse()
    {
        var json = """{"operator": "StartsWith"}""";

        var options = CreateOptions();
        var result = JsonSerializer.Deserialize<OperatorTestDto>(json, options);

        Assert.NotNull(result);
        Assert.Equal(ConditionOperator.StartsWith, result!.Operator);
    }

    [Fact]
    public void Serialize_EnumValues_UsesPascalCase()
    {
        var dto = new EnumTestDto
        {
            FlagType = FlagType.Boolean,
            ServeType = ServeType.Rollout
        };

        var options = CreateOptions();
        var json = JsonSerializer.Serialize(dto, options);

        Assert.Contains("\"Boolean\"", json);
        Assert.Contains("\"Rollout\"", json);
        Assert.DoesNotContain("\"boolean\"", json);
        Assert.DoesNotContain("\"rollout\"", json);
    }

    [Fact]
    public void EnumConverter_DoesNotInheritCamelCasePolicy()
    {
        // Verify that JsonStringEnumConverter does not inherit CamelCase from
        // PropertyNamingPolicy — enum values must remain PascalCase to match the API.
        // On .NET 8+, the no-arg JsonStringEnumConverter() can inherit the naming
        // policy from JsonSerializerOptions, causing silent deserialization failures.
        var options = CreateOptions();

        // Serialization: must produce PascalCase enum values
        var serialized = JsonSerializer.Serialize(ServeType.Fixed, options);
        Assert.Equal("\"Fixed\"", serialized);

        var serializedRollout = JsonSerializer.Serialize(ServeType.Rollout, options);
        Assert.Equal("\"Rollout\"", serializedRollout);

        // Deserialization: PascalCase from API must round-trip correctly
        var deserialized = JsonSerializer.Deserialize<ServeType>("\"Fixed\"", options);
        Assert.Equal(ServeType.Fixed, deserialized);

        var deserializedRollout = JsonSerializer.Deserialize<ServeType>("\"Rollout\"", options);
        Assert.Equal(ServeType.Rollout, deserializedRollout);
    }

    internal class EnumTestDto
    {
        public FlagType FlagType { get; set; }
        public ServeType ServeType { get; set; }
    }

    internal class OperatorTestDto
    {
        public ConditionOperator Operator { get; set; }
    }
}
