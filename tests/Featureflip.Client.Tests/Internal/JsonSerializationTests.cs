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

    [Theory]
    [InlineData("SemverEquals")]
    [InlineData("SemverGreaterThan")]
    [InlineData("SemverGreaterThanOrEqual")]
    [InlineData("SemverLessThan")]
    [InlineData("SemverLessThanOrEqual")]
    public void Deserialize_PascalCaseSemverOperators_FromApiResponse(string wireValue)
    {
        // ConditionOperator is internal, so compare by member name (string) rather than typing
        // the parameter as the enum (which a public xUnit theory method can't accept).
        var json = $$"""{"operator": "{{wireValue}}"}""";

        var options = CreateOptions();
        var result = JsonSerializer.Deserialize<OperatorTestDto>(json, options);

        Assert.NotNull(result);
        Assert.Equal(wireValue, result!.Operator.ToString());
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

    [Fact]
    public void Deserialize_FlagConfigurationWithPrerequisites_FromWireFormat()
    {
        // The Evaluation API serves flag configs with a `prerequisites` array. The SDK
        // must deserialize the camelCase property names from the wire format.
        var json = """
        {
            "key": "child-flag",
            "version": 1,
            "type": "Boolean",
            "enabled": true,
            "variations": [
                { "key": "on", "value": true },
                { "key": "off", "value": false }
            ],
            "rules": [],
            "fallthrough": { "type": "Fixed", "variation": "on" },
            "offVariation": "off",
            "prerequisites": [
                { "prerequisiteFlagKey": "parent", "expectedVariationKey": "on" },
                { "prerequisiteFlagKey": "other", "expectedVariationKey": "off" }
            ]
        }
        """;

        var options = CreateOptions();
        var flag = JsonSerializer.Deserialize<FlagConfiguration>(json, options);

        Assert.NotNull(flag);
        Assert.Equal(2, flag!.Prerequisites.Count);
        Assert.Equal("parent", flag.Prerequisites[0].PrerequisiteFlagKey);
        Assert.Equal("on", flag.Prerequisites[0].ExpectedVariationKey);
        Assert.Equal("other", flag.Prerequisites[1].PrerequisiteFlagKey);
        Assert.Equal("off", flag.Prerequisites[1].ExpectedVariationKey);
    }

    [Fact]
    public void Deserialize_FlagConfigurationWithoutPrerequisites_DefaultsToEmpty()
    {
        // Older flag configs without the prerequisites field must deserialize cleanly.
        var json = """
        {
            "key": "no-prereqs",
            "version": 1,
            "type": "Boolean",
            "enabled": true,
            "variations": [{ "key": "on", "value": true }],
            "rules": [],
            "fallthrough": { "type": "Fixed", "variation": "on" },
            "offVariation": "off"
        }
        """;

        var flag = JsonSerializer.Deserialize<FlagConfiguration>(json, CreateOptions());

        Assert.NotNull(flag);
        Assert.NotNull(flag!.Prerequisites);
        Assert.Empty(flag.Prerequisites);
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
