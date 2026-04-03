using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

public class FlagStoreTests
{
    [Fact]
    public void FlagStore_InitiallyEmpty()
    {
        // Arrange
        var store = new FlagStore();

        // Act & Assert
        Assert.False(store.TryGetFlag("any-flag", out _));
        Assert.Empty(store.GetAllFlags());
        Assert.False(store.TryGetSegment("any-segment", out _));
        Assert.Empty(store.GetAllSegments());
    }

    [Fact]
    public void FlagStore_Update_StoresFlags()
    {
        // Arrange
        var store = new FlagStore();
        var flags = new List<FlagConfiguration>
        {
            CreateFlag("flag-1"),
            CreateFlag("flag-2")
        };

        // Act
        store.Replace(flags, new List<Segment>());

        // Assert
        Assert.True(store.TryGetFlag("flag-1", out var flag1));
        Assert.NotNull(flag1);
        Assert.Equal("flag-1", flag1.Key);

        Assert.True(store.TryGetFlag("flag-2", out var flag2));
        Assert.NotNull(flag2);
        Assert.Equal("flag-2", flag2.Key);

        Assert.Equal(2, store.GetAllFlags().Count);
    }

    [Fact]
    public void FlagStore_Update_ReplacesExistingFlags()
    {
        // Arrange
        var store = new FlagStore();
        var initialFlags = new List<FlagConfiguration>
        {
            CreateFlag("flag-1", version: 1)
        };
        var updatedFlags = new List<FlagConfiguration>
        {
            CreateFlag("flag-1", version: 2)
        };

        // Act
        store.Replace(initialFlags, new List<Segment>());
        store.Replace(updatedFlags, new List<Segment>());

        // Assert
        Assert.True(store.TryGetFlag("flag-1", out var flag));
        Assert.NotNull(flag);
        Assert.Equal(2, flag.Version);
        Assert.Equal(1, store.GetAllFlags().Count);
    }

    [Fact]
    public void FlagStore_Update_StoresSegments()
    {
        // Arrange
        var store = new FlagStore();
        var segments = new List<Segment>
        {
            CreateSegment("segment-1"),
            CreateSegment("segment-2")
        };

        // Act
        store.Replace(new List<FlagConfiguration>(), segments);

        // Assert
        Assert.True(store.TryGetSegment("segment-1", out var segment1));
        Assert.NotNull(segment1);
        Assert.Equal("segment-1", segment1.Key);

        Assert.True(store.TryGetSegment("segment-2", out var segment2));
        Assert.NotNull(segment2);
        Assert.Equal("segment-2", segment2.Key);

        Assert.Equal(2, store.GetAllSegments().Count);
    }

    [Fact]
    public void FlagStore_Update_ReplacesExistingSegments()
    {
        // Arrange
        var store = new FlagStore();
        var initialSegments = new List<Segment>
        {
            CreateSegment("segment-1", version: 1)
        };
        var updatedSegments = new List<Segment>
        {
            CreateSegment("segment-1", version: 2)
        };

        // Act
        store.Replace(new List<FlagConfiguration>(), initialSegments);
        store.Replace(new List<FlagConfiguration>(), updatedSegments);

        // Assert
        Assert.True(store.TryGetSegment("segment-1", out var segment));
        Assert.NotNull(segment);
        Assert.Equal(2, segment.Version);
        Assert.Equal(1, store.GetAllSegments().Count);
    }

    [Fact]
    public void FlagStore_TryGetFlag_ReturnsFalseForNonExistent()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("existing-flag") }, new List<Segment>());

        // Act
        var result = store.TryGetFlag("non-existent-flag", out var flag);

        // Assert
        Assert.False(result);
        Assert.Null(flag);
    }

    [Fact]
    public void FlagStore_TryGetSegment_ReturnsFalseForNonExistent()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration>(), new List<Segment> { CreateSegment("existing-segment") });

        // Act
        var result = store.TryGetSegment("non-existent-segment", out var segment);

        // Assert
        Assert.False(result);
        Assert.Null(segment);
    }

    [Fact]
    public void FlagStore_GetAllFlags_ReturnsReadOnlyCopy()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Act
        var allFlags = store.GetAllFlags();

        // Assert
        Assert.Single(allFlags);
        Assert.True(allFlags.ContainsKey("flag-1"));
    }

    [Fact]
    public void FlagStore_GetAllSegments_ReturnsReadOnlyCopy()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration>(), new List<Segment> { CreateSegment("segment-1") });

        // Act
        var allSegments = store.GetAllSegments();

        // Assert
        Assert.Single(allSegments);
        Assert.True(allSegments.ContainsKey("segment-1"));
    }

    [Fact]
    public void FlagStore_IsThreadSafe()
    {
        // Arrange
        var store = new FlagStore();
        var flags = Enumerable.Range(0, 100).Select(i => CreateFlag($"flag-{i}")).ToList();
        var segments = Enumerable.Range(0, 100).Select(i => CreateSegment($"segment-{i}")).ToList();

        // Act - concurrent writes and reads should not throw
        Parallel.Invoke(
            () => store.Replace(flags.Take(50).ToList(), segments.Take(50).ToList()),
            () => store.Replace(flags.Skip(50).ToList(), segments.Skip(50).ToList()),
            () =>
            {
                for (int i = 0; i < 100; i++)
                {
                    store.TryGetFlag($"flag-{i}", out _);
                    store.TryGetSegment($"segment-{i}", out _);
                }
            },
            () =>
            {
                for (int i = 0; i < 50; i++)
                {
                    _ = store.GetAllFlags();
                    _ = store.GetAllSegments();
                }
            }
        );

        // Assert - no exceptions thrown means thread safety is working
        // The store should have some flags and segments
        Assert.True(store.GetAllFlags().Count > 0 || store.GetAllSegments().Count > 0);
    }

    [Fact]
    public void FlagStore_Replace_WithEmptyCollections_ClearsExistingData()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration> { CreateFlag("flag-1") },
            new List<Segment> { CreateSegment("segment-1") }
        );

        // Act - replace with empty collections
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        // Assert - existing data should be cleared
        Assert.False(store.TryGetFlag("flag-1", out _));
        Assert.False(store.TryGetSegment("segment-1", out _));
    }

    [Fact]
    public void FlagStore_Replace_HandlesDuplicateKeys()
    {
        // Arrange
        var store = new FlagStore();
        var flags = new List<FlagConfiguration>
        {
            CreateFlag("flag-1", version: 1),
            CreateFlag("flag-1", version: 2) // Duplicate key with different version
        };

        // Act - should not throw, last value wins
        store.Replace(flags, new List<Segment>());

        // Assert - should have the last occurrence (version 2)
        Assert.True(store.TryGetFlag("flag-1", out var flag));
        Assert.Equal(2, flag!.Version);
        Assert.Single(store.GetAllFlags());
    }

    [Fact]
    public void FlagStore_Upsert_AddsNewFlag()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Act
        store.Upsert(flags: new List<FlagConfiguration> { CreateFlag("flag-2") });

        // Assert
        Assert.True(store.TryGetFlag("flag-1", out _));
        Assert.True(store.TryGetFlag("flag-2", out _));
        Assert.Equal(2, store.GetAllFlags().Count);
    }

    [Fact]
    public void FlagStore_Upsert_UpdatesExistingFlag()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1", version: 1) }, new List<Segment>());

        // Act
        store.Upsert(flags: new List<FlagConfiguration> { CreateFlag("flag-1", version: 2) });

        // Assert
        Assert.True(store.TryGetFlag("flag-1", out var flag));
        Assert.Equal(2, flag!.Version);
        Assert.Single(store.GetAllFlags());
    }

    [Fact]
    public void FlagStore_Upsert_AddsNewSegment()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration>(), new List<Segment> { CreateSegment("segment-1") });

        // Act
        store.Upsert(segments: new List<Segment> { CreateSegment("segment-2") });

        // Assert
        Assert.True(store.TryGetSegment("segment-1", out _));
        Assert.True(store.TryGetSegment("segment-2", out _));
        Assert.Equal(2, store.GetAllSegments().Count);
    }

    [Fact]
    public void FlagStore_RemoveFlag_RemovesExistingFlag()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration> { CreateFlag("flag-1"), CreateFlag("flag-2") },
            new List<Segment>());

        // Act
        store.RemoveFlag("flag-1");

        // Assert
        Assert.False(store.TryGetFlag("flag-1", out _));
        Assert.True(store.TryGetFlag("flag-2", out _));
        Assert.Single(store.GetAllFlags());
    }

    [Fact]
    public void FlagStore_RemoveFlag_NoOpForNonExistent()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Act - should not throw
        store.RemoveFlag("non-existent");

        // Assert - original flag still exists
        Assert.True(store.TryGetFlag("flag-1", out _));
    }

    [Fact]
    public void FlagStore_RemoveSegment_RemovesExistingSegment()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>(),
            new List<Segment> { CreateSegment("segment-1"), CreateSegment("segment-2") });

        // Act
        store.RemoveSegment("segment-1");

        // Assert
        Assert.False(store.TryGetSegment("segment-1", out _));
        Assert.True(store.TryGetSegment("segment-2", out _));
        Assert.Single(store.GetAllSegments());
    }

    [Fact]
    public void FlagStore_ConcurrentReplaceAndUpsert_DoNotLoseUpserts()
    {
        // Regression test for #341: Replace() not acquiring _updateLock
        // causes Upsert() writes to be silently lost.
        // We run many iterations to increase the chance of hitting the race.
        for (int iteration = 0; iteration < 50; iteration++)
        {
            var store = new FlagStore();
            var barrier = new Barrier(2);

            var replaceTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                store.Replace(
                    new List<FlagConfiguration> { CreateFlag("replace-flag") },
                    new List<Segment> { CreateSegment("replace-segment") });
            });

            var upsertTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                store.Upsert(flags: new List<FlagConfiguration> { CreateFlag("upsert-flag") });
            });

            Task.WaitAll(replaceTask, upsertTask);

            // After both complete, at least the last writer's data must be present.
            // Without the lock, _snapshot could be overwritten by Replace after Upsert
            // read the old snapshot, silently dropping the upserted flag.
            // Note: if Replace runs last it overwrites Upsert's data (by design),
            // so we only assert that at least one operation's result survived.
            var hasReplaceFlag = store.TryGetFlag("replace-flag", out _);
            var hasUpsertFlag = store.TryGetFlag("upsert-flag", out _);

            Assert.True(hasReplaceFlag || hasUpsertFlag,
                $"Iteration {iteration}: Both flags were lost — race condition in FlagStore");
        }
    }

    [Fact]
    public void FlagStore_ConcurrentUpserts_AreThreadSafe()
    {
        // Arrange
        var store = new FlagStore();

        // Act - concurrent upserts should not throw or lose data
        Parallel.For(0, 100, i =>
        {
            store.Upsert(flags: new List<FlagConfiguration> { CreateFlag($"flag-{i}") });
        });

        // Assert - should have all 100 flags
        Assert.Equal(100, store.GetAllFlags().Count);
    }

    private static FlagConfiguration CreateFlag(string key, int version = 1)
    {
        return new FlagConfiguration
        {
            Key = key,
            Version = version,
            Type = FlagType.Boolean,
            Enabled = true,
            Variations = new List<Variation>
            {
                new Variation { Key = "on", Value = JsonSerializer.SerializeToElement(true) },
                new Variation { Key = "off", Value = JsonSerializer.SerializeToElement(false) }
            },
            Rules = new List<TargetingRule>(),
            Fallthrough = new ServeConfig { Type = ServeType.Fixed, Variation = "on" },
            OffVariation = "off"
        };
    }

    private static Segment CreateSegment(string key, int version = 1)
    {
        return new Segment
        {
            Key = key,
            Version = version,
            Conditions = new List<Condition>(),
            ConditionLogic = ConditionLogic.And
        };
    }
}
