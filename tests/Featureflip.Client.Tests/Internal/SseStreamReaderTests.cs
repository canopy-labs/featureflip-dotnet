using System.Text;
using Featureflip.Client.Internal;
using Featureflip.Client.Tests.TestSupport;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task Sync_ParsesFullSnapshot()
    {
        // The connect-time full snapshot the server sends first on (re)connect —
        // same shape as GET /v1/sdk/flags. Carried whole so the core can REPLACE.
        var snapshot = """{"environment":"test","version":5,"flags":[{"key":"flag-new","version":5,"type":"Boolean","enabled":true,"variations":[{"key":"on","value":true},{"key":"off","value":false}],"rules":[],"fallthrough":{"type":"Fixed","variation":"on"},"offVariation":"off"}],"segments":[]}""";
        var sse = SseMessage("sync", snapshot);
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Single(events);
        Assert.Equal(SseEventType.Sync, events[0].Type);
        Assert.NotNull(events[0].Snapshot);
        Assert.Contains(events[0].Snapshot!.Flags, f => f.Key == "flag-new");
    }

    [Fact]
    public async Task Sync_DataSplitAcrossMultipleLines_IsJoinedBeforeParsing()
    {
        // Per the SSE spec, multiple `data:` lines in one event join with "\n".
        // Split a sync snapshot after a comma so neither line is valid JSON alone —
        // an overwrite instead of a join would drop the second half. Mirrors go's
        // TestStreaming_ConcatenatesMultiLineData; covers SseStreamReader.cs:95-99.
        var sse = "event: sync\n"
            + "data: {\"environment\":\"test\",\"version\":7,\"flags\":[{\"key\":\"split-flag\",\"version\":7,\n"
            + "data: \"type\":\"Boolean\",\"enabled\":true,\"variations\":[{\"key\":\"on\",\"value\":true},"
              + "{\"key\":\"off\",\"value\":false}],\"rules\":[],\"fallthrough\":{\"type\":\"Fixed\","
              + "\"variation\":\"on\"},\"offVariation\":\"off\"}],\"segments\":[]}\n"
            + "\n";
        using var reader = CreateReader(sse);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Single(events);
        Assert.Equal(SseEventType.Sync, events[0].Type);
        Assert.NotNull(events[0].Snapshot);
        Assert.Contains(events[0].Snapshot!.Flags, f => f.Key == "split-flag");
    }

    [Fact]
    public async Task Sync_LargeSnapshot_OverChunkedStream_IsFullyReassembled()
    {
        // The shared >64 KiB sync fixture, delivered in tiny chunks, proves
        // StreamReader.ReadLineAsync reassembles an oversized single `data:` line
        // across read boundaries and ParseSyncEvent deserializes the full snapshot.
        var fixture = (await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "golden", "snapshot.json"))).Trim();
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(fixture) > 64 * 1024,
            "snapshot.json fixture must exceed 64 KiB");

        var sse = $"event: sync\ndata: {fixture}\n\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(sse);
        using var reader = new SseStreamReader(new ChunkedStream(bytes, 64), NullLogger.Instance);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Single(events);
        Assert.Equal(SseEventType.Sync, events[0].Type);
        Assert.NotNull(events[0].Snapshot);
        Assert.Contains(events[0].Snapshot!.Flags, f => f.Key == "sync-canary");
        Assert.True(events[0].Snapshot!.Flags.Count > 800,
            "the full multi-flag snapshot should be parsed, not truncated at a chunk boundary");
        // The snapshot's structural payload (not just flag keys) round-trips: a
        // dropped segment/rich field would otherwise be swallowed by unknown-member skip.
        Assert.Contains(events[0].Snapshot!.Segments, s => s.Key == "snapshot-segment");
    }

    [Fact]
    public async Task MidStreamReadError_IsLoggedAtDebug_NotWarning()
    {
        // A stream severed mid-frame is the routine case behind a CDN (#2457): the
        // reader stops, the core reconnects and re-syncs, and nothing is degraded.
        // Reporting that at Warning turned ordinary operation into ~10 alarming
        // lines/day/env, so the severity has to say "expected", not "problem".
        var logger = new LevelCapturingLogger();
        using var reader = new SseStreamReader(new SeveringStream(), logger);

        var events = new List<SseEvent>();
        await foreach (var e in reader.ReadEventsAsync())
            events.Add(e);

        Assert.Empty(events);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug);
    }

    // A read-only Stream that fails the way a severed SSE connection does: the body
    // ends mid-frame with an IOException rather than a clean EOF. This is the shape
    // HttpIOException("The response ended prematurely") arrives in.
    private sealed class SeveringStream : Stream
    {
        private static IOException Severed() => new("The response ended prematurely.");

        public override int Read(byte[] buffer, int offset, int count) => throw Severed();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => throw Severed();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => throw Severed();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A read-only Stream that returns at most `chunk` bytes per read, forcing
    // ReadLineAsync to reassemble a line across many underlying reads.
    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _pos;
        public ChunkedStream(byte[] data, int chunk) { _data = data; _chunk = chunk; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _data.Length) return 0;
            var n = Math.Min(Math.Min(_chunk, count), _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= _data.Length) return new ValueTask<int>(0);
            var n = Math.Min(Math.Min(_chunk, buffer.Length), _data.Length - _pos);
            _data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return new ValueTask<int>(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
