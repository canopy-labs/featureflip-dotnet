using System.Text.Json;
using System.Text.Json.Serialization;

namespace Featureflip.Client.Internal.Models;

internal sealed record SdkEvent
{
    public string Type { get; init; } = string.Empty;
    public string FlagKey { get; init; } = string.Empty;
    public string? UserId { get; init; }
    // Absent optional fields are omitted from the wire rather than sent as null, matching the
    // other SDKs' evaluation-event payload (#4268). UserId deliberately keeps serializing as
    // null for anonymous contexts.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Variation { get; init; }
    public DateTime Timestamp { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>Kebab-case reason category (rule-match, fallthrough, ...). Evaluation events only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>The matched rule's id, only when <see cref="Reason"/> is rule-match.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuleId { get; init; }

    /// <summary>The failing prerequisite's key, only when <see cref="Reason"/> is prerequisite-failed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrerequisiteKey { get; init; }
}
