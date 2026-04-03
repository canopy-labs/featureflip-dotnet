using System.Text;
using Featureflip.Client.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

public class SseStreamReaderTests
{
    private SseStreamReader CreateReader(string sseContent)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseContent));
        return new SseStreamReader(stream, NullLogger.Instance);
    }

    private static string SseMessage(string eventType, string data)
        => $"event: {eventType}\ndata: {data}\n\n";

    [Fact]
    public async Task FlagCreated_ParsesKeyAndVersion()
    {
        var sse = SseMessage("flag.created", """{"key":"new-flag","version":1}""");
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Single(events);
        Assert.Equal(SseEventType.FlagCreated, events[0].Type);
        Assert.Equal("new-flag", events[0].FlagKey);
        Assert.Equal(1, events[0].Version);
    }

    [Fact]
    public async Task FlagUpdated_ParsesKeyAndVersion()
    {
        var sse = SseMessage("flag.updated", """{"key":"my-flag","version":2}""");
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Single(events);
        Assert.Equal(SseEventType.FlagUpdated, events[0].Type);
        Assert.Equal("my-flag", events[0].FlagKey);
        Assert.Equal(2, events[0].Version);
    }

    [Fact]
    public async Task FlagDeleted_ParsesKey()
    {
        var sse = SseMessage("flag.deleted", """{"key":"doomed-flag"}""");
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Single(events);
        Assert.Equal(SseEventType.FlagDeleted, events[0].Type);
        Assert.Equal("doomed-flag", events[0].FlagKey);
    }

    [Fact]
    public async Task SegmentUpdated_HasNoFlagKey()
    {
        var sse = SseMessage("segment.updated", """{"key":"seg-1","version":1}""");
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Single(events);
        Assert.Equal(SseEventType.SegmentUpdated, events[0].Type);
        Assert.Null(events[0].FlagKey);
    }

    [Fact]
    public async Task Ping_IsParsed()
    {
        var sse = SseMessage("ping", """{"timestamp":"2026-03-13T00:00:00Z"}""");
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Single(events);
        Assert.Equal(SseEventType.Ping, events[0].Type);
    }

    [Fact]
    public async Task UnknownEventType_IsIgnored()
    {
        var sse = SseMessage("some.unknown", """{"key":"x"}""");
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Empty(events);
    }

    [Fact]
    public async Task OldEventNames_AreIgnored()
    {
        var sse = SseMessage("put", """{"flags":[]}""")
            + SseMessage("patch", """{"path":"/flags/x","data":{}}""")
            + SseMessage("delete", """{"path":"/flags/x"}""")
            + SseMessage("flag-updated", """{"key":"x"}""");
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Empty(events);
    }

    [Fact]
    public async Task InvalidJson_IsSkipped()
    {
        var sse = SseMessage("flag.updated", "not-json{{{")
            + SseMessage("flag.created", """{"key":"good-flag","version":1}""");
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        // The invalid event is skipped, the valid one is parsed
        Assert.Single(events);
        Assert.Equal(SseEventType.FlagCreated, events[0].Type);
        Assert.Equal("good-flag", events[0].FlagKey);
    }

    [Fact]
    public async Task MissingKeyInPayload_StillParsesWithNullKey()
    {
        var sse = SseMessage("flag.updated", """{"version":1}""");
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Single(events);
        Assert.Equal(SseEventType.FlagUpdated, events[0].Type);
        Assert.Null(events[0].FlagKey);
    }

    [Fact]
    public async Task MultipleEvents_AllParsed()
    {
        var sse = SseMessage("flag.created", """{"key":"flag-a","version":1}""")
            + SseMessage("flag.updated", """{"key":"flag-b","version":2}""")
            + SseMessage("flag.deleted", """{"key":"flag-c"}""")
            + SseMessage("segment.updated", """{"key":"seg-1"}""");
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Equal(4, events.Count);
        Assert.Equal(SseEventType.FlagCreated, events[0].Type);
        Assert.Equal(SseEventType.FlagUpdated, events[1].Type);
        Assert.Equal(SseEventType.FlagDeleted, events[2].Type);
        Assert.Equal(SseEventType.SegmentUpdated, events[3].Type);
    }

    [Fact]
    public async Task CommentLines_AreIgnored()
    {
        var sse = ": this is a comment\n"
            + SseMessage("flag.updated", """{"key":"my-flag","version":1}""");
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Single(events);
        Assert.Equal("my-flag", events[0].FlagKey);
    }
}
