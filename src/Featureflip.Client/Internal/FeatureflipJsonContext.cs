using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Featureflip.Client.Internal.Models;

namespace Featureflip.Client.Internal;

/// <summary>
/// Source-generated System.Text.Json metadata for every type the SDK itself reads or
/// writes, so no SDK-owned (de)serialization depends on reflection (#4301).
/// </summary>
/// <remarks>
/// <para>
/// An app with reflection-based serialization disabled — NativeAOT, a trimmed app, a .NET 10
/// file-based app (<c>dotnet run app.cs</c>), or
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> — throws
/// <see cref="InvalidOperationException"/> from any reflection-backed call. Before this
/// context existed that was every flag fetch and every event post, so such an app served
/// every flag's default and dropped every event, with no diagnostic under the default
/// <c>NullLogger</c>.
/// </para>
/// <para>
/// Deliberately carries NO <see cref="JsonSourceGenerationOptionsAttribute"/>: each call
/// site binds its own instance to its own <see cref="JsonSerializerOptions"/> (camelCase and
/// the tolerant enum converters for wire traffic, case-insensitive for the snapshot frame,
/// plain for the change-detection comparison), and the generated metadata honours the
/// options it is bound to. Baking options in here would let the wire options and the
/// generated ones drift apart. Property-level attributes on the models
/// (<see cref="JsonIgnoreAttribute"/> on <see cref="SdkEvent"/>) are captured by the
/// generator, so the wire bytes are the same as the reflection path produced.
/// </para>
/// <para>
/// Adding a type to a wire model needs nothing here: the generator walks the graph from
/// each root below. A NEW root — a new payload the SDK parses or posts — must be listed,
/// or that call fails with <see cref="NotSupportedException"/> in a reflection-free app,
/// which the <c>Featureflip.Client.ReflectionFreeTests</c> project exists to catch.
/// </para>
/// <para>
/// The JSON DOM types and the extra numeric types are here for the caller-facing
/// <c>Variation&lt;T&gt;</c>/<c>JsonVariation&lt;T&gt;</c> path: they let a reflection-free
/// app read a JSON flag as <see cref="JsonElement"/>/<see cref="JsonNode"/> or a number as
/// <see cref="long"/>/<see cref="decimal"/>. A caller's own POCO cannot be generated here;
/// see <c>SharedFeatureflipCore.DeserializeValue</c> for how that degrades.
/// </para>
/// </remarks>
[JsonSerializable(typeof(GetFlagsResponse))]
[JsonSerializable(typeof(FlagConfiguration))]
[JsonSerializable(typeof(Segment))]
[JsonSerializable(typeof(SendEventsRequest))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonDocument))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(decimal))]
internal sealed partial class FeatureflipJsonContext : JsonSerializerContext
{
}
