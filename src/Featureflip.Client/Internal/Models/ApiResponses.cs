namespace Featureflip.Client.Internal.Models;

internal sealed record GetFlagsResponse
{
    public string Environment { get; init; } = string.Empty;
    public int Version { get; init; }
    public List<FlagConfiguration> Flags { get; init; } = new();
    public List<Segment> Segments { get; init; } = new();
}
