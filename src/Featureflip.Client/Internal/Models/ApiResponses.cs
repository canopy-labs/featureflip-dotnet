namespace Featureflip.Client.Internal.Models;

internal sealed record GetFlagsResponse
{
    private readonly List<FlagConfiguration> _flags = new();
    private readonly List<Segment> _segments = new();

    public string Environment { get; init; } = string.Empty;
    public int Version { get; init; }

    // An explicit `"flags": null` / `"segments": null` payload makes
    // System.Text.Json invoke the init accessor with null, overriding the
    // initializer above. Normalize here so no consumer can observe a null
    // collection — the server sends `[]`, but a null must degrade to empty
    // rather than throwing deep inside a refresh path.
    public List<FlagConfiguration> Flags
    {
        get => _flags;
        init => _flags = value ?? new();
    }

    public List<Segment> Segments
    {
        get => _segments;
        init => _segments = value ?? new();
    }
}
