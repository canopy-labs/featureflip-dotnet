namespace Featureflip.Client.Internal.Models;

/// <remarks>
/// The wire models below use <c>set</c>, not <c>init</c>, on purpose. They are deserialized
/// through the source-generated <see cref="FeatureflipJsonContext"/> (#4301), and that
/// generator assigns init-only properties through an object initializer that sets EVERY one
/// of them, so a property ABSENT from the payload is overwritten with null instead of keeping
/// its <c>= new()</c> initializer. A flag sent without <c>prerequisites</c> then crashed the
/// evaluator. With a setter, an absent property is simply not assigned. Nothing mutates these
/// after parsing; <c>with</c> expressions still work.
/// </remarks>
internal sealed record GetFlagsResponse
{
    private List<FlagConfiguration> _flags = new();
    private List<Segment> _segments = new();

    public string Environment { get; set; } = string.Empty;
    public int Version { get; set; }

    // An explicit `"flags": null` / `"segments": null` payload makes
    // System.Text.Json invoke the setter with null, overriding the
    // initializer above. Normalize here so no consumer can observe a null
    // collection — the server sends `[]`, but a null must degrade to empty
    // rather than throwing deep inside a refresh path.
    public List<FlagConfiguration> Flags
    {
        get => _flags;
        set => _flags = value ?? new();
    }

    public List<Segment> Segments
    {
        get => _segments;
        set => _segments = value ?? new();
    }
}
