using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

public class EventProcessorTests
{
    [Fact]
    public void EventProcessor_EnqueuesEvents()
    {
        // Arrange
        var processor = new EventProcessor(TimeSpan.FromMinutes(10), 100);
        var evt = CreateEvent("test-flag");

        // Act
        processor.Enqueue(evt);
        var events = processor.Drain();

        // Assert
        Assert.Single(events);
        Assert.Equal("test-flag", events[0].FlagKey);
    }

    [Fact]
    public void EventProcessor_Drain_ClearsQueue()
    {
        // Arrange
        var processor = new EventProcessor(TimeSpan.FromMinutes(10), 100);
        processor.Enqueue(CreateEvent("flag-1"));
        processor.Enqueue(CreateEvent("flag-2"));

        // Act
        var firstDrain = processor.Drain();
        var secondDrain = processor.Drain();

        // Assert
        Assert.Equal(2, firstDrain.Count);
        Assert.Empty(secondDrain);
    }

    [Fact]
    public void EventProcessor_IsThreadSafe()
    {
        // Arrange
        var processor = new EventProcessor(TimeSpan.FromMinutes(10), 100);

        // Act - concurrent enqueues should not throw
        Parallel.For(0, 100, i => processor.Enqueue(CreateEvent($"flag-{i}")));
        var events = processor.Drain();

        // Assert
        Assert.Equal(100, events.Count);
    }

    [Fact]
    public void EventProcessor_ShouldFlush_ReturnsTrueWhenBatchSizeReached()
    {
        // Arrange
        var processor = new EventProcessor(TimeSpan.FromMinutes(10), batchSize: 3);

        // Act & Assert
        processor.Enqueue(CreateEvent("flag-1"));
        Assert.False(processor.ShouldFlush());

        processor.Enqueue(CreateEvent("flag-2"));
        Assert.False(processor.ShouldFlush());

        processor.Enqueue(CreateEvent("flag-3"));
        Assert.True(processor.ShouldFlush());
    }

    [Fact]
    public void EventProcessor_ShouldFlush_ReturnsFalseForEmptyQueue()
    {
        // Arrange
        var processor = new EventProcessor(TimeSpan.FromMinutes(10), batchSize: 1);

        // Act & Assert
        Assert.False(processor.ShouldFlush());
    }

    [Fact]
    public void EventProcessor_ShouldFlush_ReturnsTrueWhenExceedsBatchSize()
    {
        // Arrange
        var processor = new EventProcessor(TimeSpan.FromMinutes(10), batchSize: 2);

        // Add more events than batch size
        processor.Enqueue(CreateEvent("flag-1"));
        processor.Enqueue(CreateEvent("flag-2"));
        processor.Enqueue(CreateEvent("flag-3"));

        // Act & Assert
        Assert.True(processor.ShouldFlush());
    }

    [Fact]
    public void EventProcessor_Dispose_StopsAcceptingNewEvents()
    {
        // Arrange
        var processor = new EventProcessor(TimeSpan.FromMinutes(10), 100);
        processor.Enqueue(CreateEvent("flag-1"));

        // Act
        processor.Dispose();
        processor.Enqueue(CreateEvent("flag-2")); // Should be ignored

        var events = processor.Drain();

        // Assert
        Assert.Single(events);
        Assert.Equal("flag-1", events[0].FlagKey);
    }

    [Fact]
    public void EventProcessor_Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var processor = new EventProcessor(TimeSpan.FromMinutes(10), 100);

        // Act & Assert - should not throw
        processor.Dispose();
        processor.Dispose();
    }

    [Fact]
    public void EventProcessor_Drain_PreservesEventOrder()
    {
        // Arrange
        var processor = new EventProcessor(TimeSpan.FromMinutes(10), 100);

        // Act
        for (int i = 0; i < 10; i++)
        {
            processor.Enqueue(CreateEvent($"flag-{i}"));
        }

        var events = processor.Drain();

        // Assert
        Assert.Equal(10, events.Count);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal($"flag-{i}", events[i].FlagKey);
        }
    }

    [Fact]
    public void EventProcessor_Drain_ReturnsReadOnlyList()
    {
        // Arrange
        var processor = new EventProcessor(TimeSpan.FromMinutes(10), 100);
        processor.Enqueue(CreateEvent("flag-1"));

        // Act
        var events = processor.Drain();

        // Assert
        Assert.IsAssignableFrom<IReadOnlyList<SdkEvent>>(events);
    }

    [Fact]
    public void EventProcessor_Enqueue_PreservesEventData()
    {
        // Arrange
        var processor = new EventProcessor(TimeSpan.FromMinutes(10), 100);
        var timestamp = DateTime.UtcNow;
        var evt = new SdkEvent
        {
            Type = "Evaluation",
            FlagKey = "test-flag",
            UserId = "user-123",
            Variation = "on",
            Timestamp = timestamp,
            Metadata = new Dictionary<string, JsonElement>
            {
                ["custom"] = JsonSerializer.SerializeToElement("value")
            }
        };

        // Act
        processor.Enqueue(evt);
        var events = processor.Drain();

        // Assert
        Assert.Single(events);
        var result = events[0];
        Assert.Equal("Evaluation", result.Type);
        Assert.Equal("test-flag", result.FlagKey);
        Assert.Equal("user-123", result.UserId);
        Assert.Equal("on", result.Variation);
        Assert.Equal(timestamp, result.Timestamp);
        Assert.NotNull(result.Metadata);
        Assert.True(result.Metadata.ContainsKey("custom"));
    }

    private static SdkEvent CreateEvent(string flagKey)
    {
        return new SdkEvent
        {
            Type = "Evaluation",
            FlagKey = flagKey,
            UserId = "user-123",
            Variation = "on",
            Timestamp = DateTime.UtcNow
        };
    }
}
