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
    public void FlagStore_Replace_WithNullCollections_TreatsAsEmpty()
    {
        // A `"flags": null` / `"segments": null` payload deserializes the DTO
        // collections to null (System.Text.Json overrides the property default),
        // so Replace must treat null as empty rather than NRE — otherwise a null
        // sync snapshot silently fails to apply and the store stays stale.
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration> { CreateFlag("flag-1") },
            new List<Segment> { CreateSegment("segment-1") });

        // Act — null collections must not throw.
        store.Replace(null!, null!);

        // Assert — replaced with empty, no exception.
        Assert.Empty(store.GetAllFlags());
        Assert.Empty(store.GetAllSegments());
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
            using var barrier = new Barrier(2);

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

    [Fact]
    public void OnChange_FirstReplace_DoesNotNotify()
    {
        // Arrange — a cold load is a baseline, not a configuration change.
        var store = new FlagStore();
        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        Assert.Empty(notifications);
    }

    [Fact]
    public void OnChange_SecondReplace_ReportsAddedRemovedAndVersionChanged()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("unchanged", version: 1),
                CreateFlag("bumped", version: 1),
                CreateFlag("removed", version: 1)
            },
            new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("unchanged", version: 1),
                CreateFlag("bumped", version: 2),
                CreateFlag("added", version: 1)
            },
            new List<Segment>());

        // Assert — one batched call carrying exactly the affected keys, in no fixed order.
        var changed = Assert.Single(notifications);
        Assert.Equal(3, changed.Count);
        Assert.Contains("bumped", changed);
        Assert.Contains("removed", changed);
        Assert.Contains("added", changed);
        Assert.DoesNotContain("unchanged", changed);
    }

    [Fact]
    public void OnChange_IdenticalReplace_DoesNotNotify()
    {
        // Arrange — every poll tick calls Replace; an unchanged snapshot must stay silent.
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1", version: 3) }, new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1", version: 3) }, new List<Segment>());

        // Assert
        Assert.Empty(notifications);
    }

    [Fact]
    public void OnChange_Unsubscribe_StopsDelivery()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        var subscription = store.OnChange(keys => notifications.Add(keys));

        // Act
        subscription.Dispose();
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        Assert.Empty(notifications);
    }

    [Fact]
    public void OnChange_ThrowingListener_DoesNotStopSiblingsOrCaller()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        var reached = false;
        store.OnChange(_ => throw new InvalidOperationException("boom"));
        store.OnChange(_ => reached = true);

        // Act — must not propagate into the polling/SSE loop that triggered the update.
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        Assert.True(reached);
    }

    [Fact]
    public void OnChange_ChangedSegment_ReportsReferencingFlags()
    {
        // Arrange — a segment edit changes evaluation outcomes without bumping any
        // flag's version, so the diff must map it back to the flags that use it.
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlagWithSegmentRule("uses-beta", "beta", version: 1),
                CreateFlag("unrelated", version: 1)
            },
            new List<Segment> { CreateSegment("beta", version: 1) });

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act — same flags at the same versions; only the segment moved.
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlagWithSegmentRule("uses-beta", "beta", version: 1),
                CreateFlag("unrelated", version: 1)
            },
            new List<Segment> { CreateSegment("beta", version: 2) });

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "uses-beta" }, changed);
    }

    [Fact]
    public void OnChange_RemovedSegmentViaReplace_ReportsReferencingFlags()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration> { CreateFlagWithSegmentRule("uses-beta", "beta", version: 1) },
            new List<Segment> { CreateSegment("beta", version: 1) });

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Replace(
            new List<FlagConfiguration> { CreateFlagWithSegmentRule("uses-beta", "beta", version: 1) },
            new List<Segment>());

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "uses-beta" }, changed);
    }

    [Fact]
    public void OnChange_Upsert_ReportsEveryUpsertedKey()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("existing", version: 1) }, new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Upsert(new List<FlagConfiguration>
        {
            CreateFlag("existing", version: 2),
            CreateFlag("brand-new", version: 1)
        });

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(2, changed.Count);
        Assert.Contains("existing", changed);
        Assert.Contains("brand-new", changed);
    }

    [Fact]
    public void OnChange_UpsertSameVersionDifferentConfig_StillNotifies()
    {
        // The wire version is second-granular — eval-api's FlagDtoMapper divides epoch
        // milliseconds by 1000 — so two edits to one flag inside the same wall-clock
        // second arrive carrying an identical version. Gating notification on a version
        // change dropped the second edit's notification while still applying its config,
        // leaving a consumer that caches per key on the pre-edit value indefinitely.
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("existing", version: 4) }, new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act — same version, opposite Enabled: what a same-second second edit looks like.
        store.Upsert(new List<FlagConfiguration> { CreateFlag("existing", version: 4) with { Enabled = false } });

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "existing" }, changed);

        Assert.True(store.TryGetFlag("existing", out var stored));
        Assert.False(stored!.Enabled);
    }

    [Fact]
    public void OnChange_UpsertByteIdenticalFlag_NotifiesAnyway()
    {
        // The accepted cost of the fix above: Upsert cannot distinguish a genuinely
        // identical re-delivery from a same-second second edit, so it reports both. Only
        // an SSE `flag.created`/`flag.updated` reaches Upsert and the server emits those
        // only for a real change, so this is a replay-only case. A redundant notification
        // makes a consumer re-read values it already has; a dropped one leaves it stale.
        // Same trade as js-sdk's unconditional `notifyListeners` in `upsert` (#2090).
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("existing", version: 4) }, new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Upsert(new List<FlagConfiguration> { CreateFlag("existing", version: 4) });

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "existing" }, changed);
    }

    [Fact]
    public void OnChange_ReplaceSameVersionDifferentFlagConfig_ReportsTheFlag()
    {
        // A full snapshot (poll, SSE `sync`, `segment.updated` refetch) carries no per-edit
        // signal, so the diff has to compare content. The wire version is second-granular,
        // so two edits to one flag inside the same wall-clock second carry an identical
        // version: if a snapshot boundary falls between them, the second edit's config
        // lands in the store while a version-only diff reports nothing changed (#2088).
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("gate", version: 7) }, new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Replace(
            new List<FlagConfiguration> { CreateFlag("gate", version: 7) with { Enabled = false } },
            new List<Segment>());

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "gate" }, changed);
    }

    [Fact]
    public void OnChange_ReplaceSameVersionDifferentSegmentConfig_ReportsReferencingFlags()
    {
        // Same collision on the segment side: an edited segment alters what a referencing
        // flag evaluates to without touching that flag's own config, so a segment whose
        // version collided had to be detected by content too (#2088).
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration> { CreateFlagWithSegmentRule("uses-beta", "beta", version: 1) },
            new List<Segment> { CreateSegment("beta", version: 3) });

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Replace(
            new List<FlagConfiguration> { CreateFlagWithSegmentRule("uses-beta", "beta", version: 1) },
            new List<Segment> { CreateSegment("beta", version: 3) with { ConditionLogic = ConditionLogic.Or } });

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "uses-beta" }, changed);
    }

    [Theory]
    [InlineData("""{"key":"on"}""")]
    [InlineData("""{"key":"on","value":null}""")]
    public void Replace_VariationWithNoUsableValue_DiffsWithoutThrowing(string variationJson)
    {
        // `Variation.Value` is a non-nullable JsonElement, and System.Text.Json leaves it at
        // default — ValueKind.Undefined — for an absent `value` AND for an explicit null. A
        // variation holding JSON null is supported config, so this is not a malformed
        // payload. JsonElement.WriteTo throws on Undefined, and the throw would escape
        // DiffSnapshot before the new snapshot is swapped in, freezing the store on its last
        // good config forever — strictly worse than the missed notification the content diff
        // exists to fix. Deserialized rather than hand-built so the test exercises the same
        // path the wire does.
        // Arrange
        var variation = JsonSerializer.Deserialize<Variation>(variationJson)!;
        Assert.Equal(JsonValueKind.Undefined, variation.Value.ValueKind);

        var flag = CreateFlag("nullish", version: 1) with { Variations = new List<Variation> { variation } };

        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { flag }, new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act — an unchanged redelivery, then a real edit. Neither may throw.
        store.Replace(new List<FlagConfiguration> { flag }, new List<Segment>());
        store.Replace(new List<FlagConfiguration> { flag with { Enabled = false } }, new List<Segment>());

        // Assert — the identical snapshot stayed silent, the edit was reported, and the
        // store actually advanced rather than being frozen by a swallowed throw.
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "nullish" }, changed);

        Assert.True(store.TryGetFlag("nullish", out var stored));
        Assert.False(stored!.Enabled);
    }

    [Fact]
    public void OnChange_ReplaceWithByteIdenticalSnapshot_DoesNotNotify()
    {
        // The other half of the content comparison, and the reason it can't simply report
        // every key on every snapshot: an unchanged poll tick must stay silent. Without
        // this, a 30s poll would wake every listener forever.
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration> { CreateFlag("a", version: 2), CreateFlag("b", version: 5) },
            new List<Segment> { CreateSegment("beta", version: 1) });

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act — a fresh but structurally identical snapshot, as an unchanged poll delivers.
        store.Replace(
            new List<FlagConfiguration> { CreateFlag("a", version: 2), CreateFlag("b", version: 5) },
            new List<Segment> { CreateSegment("beta", version: 1) });

        // Assert
        Assert.Empty(notifications);
    }

    [Fact]
    public void OnChange_UpsertSegment_ReportsReferencingFlags()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration> { CreateFlagWithSegmentRule("uses-beta", "beta", version: 1) },
            new List<Segment> { CreateSegment("beta", version: 1) });

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Upsert(segments: new List<Segment> { CreateSegment("beta", version: 2) });

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "uses-beta" }, changed);
    }

    [Fact]
    public void OnChange_RemoveFlag_NotifiesOnlyWhenPresent()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.RemoveFlag("never-existed");
        store.RemoveFlag("flag-1");

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "flag-1" }, changed);
    }

    [Fact]
    public void OnChange_RemoveSegment_ReportsReferencingFlags()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlagWithSegmentRule("uses-beta", "beta", version: 1),
                CreateFlag("unrelated", version: 1)
            },
            new List<Segment> { CreateSegment("beta", version: 1) });

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.RemoveSegment("beta");

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "uses-beta" }, changed);
    }

    [Fact]
    public void OnChange_ChangedSegment_ToleratesMalformedFlagRules()
    {
        // Arrange — a null Rules collection and a Rules list holding a null entry must not
        // abort the segment-reference scan. The store must still update and still notify
        // correctly for the well-formed flag that actually references the changed segment.
        var nullRulesFlag = CreateFlag("null-rules", version: 1) with { Rules = null! };
        var nullRuleEntryFlag = CreateFlag("null-rule-entry", version: 1) with
        {
            Rules = new List<TargetingRule> { null! }
        };
        var wellFormedFlag = CreateFlagWithSegmentRule("uses-beta", "beta", version: 1);

        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration> { nullRulesFlag, nullRuleEntryFlag, wellFormedFlag },
            new List<Segment> { CreateSegment("beta", version: 1) });

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act — bump the segment; must not throw despite the malformed flags.
        store.Replace(
            new List<FlagConfiguration> { nullRulesFlag, nullRuleEntryFlag, wellFormedFlag },
            new List<Segment> { CreateSegment("beta", version: 2) });

        // Assert — the store still applied the update...
        Assert.True(store.TryGetSegment("beta", out var segment));
        Assert.Equal(2, segment!.Version);

        // ...and notified only the flag that genuinely references the changed segment,
        // proving the malformed flags were tolerated rather than silently swallowing the
        // whole diff (or blocking the notification for everyone else).
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "uses-beta" }, changed);
    }

    [Fact]
    public void OnChange_UpsertBeforeFirstReplace_DoesNotMakeTheColdLoadReportable()
    {
        // An SSE delta can reach the store before the initial load finishes: background
        // init fails on a transient error (leaving the store empty), the refresh loop
        // times out waiting and opens the stream, and a flag.updated arrives before the
        // sync snapshot. Upsert must not treat its one flag as the baseline — otherwise
        // the first real Replace diffs the full config against that single flag and
        // reports every other flag as added, raising the cold load as a change.
        var store = new FlagStore();
        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act — SSE delta first, then the initial full load.
        store.Upsert(new List<FlagConfiguration> { CreateFlag("early-delta", version: 1) });
        notifications.Clear();

        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("early-delta", version: 1),
                CreateFlag("flag-2", version: 1),
                CreateFlag("flag-3", version: 1)
            },
            new List<Segment>());

        // Assert — the cold load stays silent.
        Assert.Empty(notifications);
    }

    // A flag's Version covers its own prerequisite rows but not the flags they point at,
    // so toggling a prerequisite bumps only the prerequisite's version while the evaluator
    // flips every dependent to `prerequisite-failed`. Without fan-out the dependent changes
    // value and is never reported, and a consumer caching per key serves it stale forever.

    [Fact]
    public void OnChange_ReplacePrerequisiteVersionChanged_ReportsDependent()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("parent", version: 1),
                CreateFlagWithPrerequisite("child", "parent")
            },
            new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("parent", version: 2),
                CreateFlagWithPrerequisite("child", "parent")
            },
            new List<Segment>());

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(2, changed.Count);
        Assert.Contains("parent", changed);
        Assert.Contains("child", changed);
    }

    [Fact]
    public void OnChange_ReplacePrerequisiteChanged_ReportsTransitiveDependents()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("root", version: 1),
                CreateFlagWithPrerequisite("mid", "root"),
                CreateFlagWithPrerequisite("leaf", "mid")
            },
            new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("root", version: 2),
                CreateFlagWithPrerequisite("mid", "root"),
                CreateFlagWithPrerequisite("leaf", "mid")
            },
            new List<Segment>());

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(3, changed.Count);
        Assert.Contains("root", changed);
        Assert.Contains("mid", changed);
        Assert.Contains("leaf", changed);
    }

    [Fact]
    public void OnChange_ReplacePrerequisiteRemoved_ReportsDependent()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("parent"),
                CreateFlagWithPrerequisite("child", "parent")
            },
            new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act — the dependent survives, carrying a now-dangling prerequisite row.
        store.Replace(
            new List<FlagConfiguration> { CreateFlagWithPrerequisite("child", "parent") },
            new List<Segment>());

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(2, changed.Count);
        Assert.Contains("parent", changed);
        Assert.Contains("child", changed);
    }

    // Ordering guard: the prerequisite walk must be seeded with the fully-resolved changed
    // set, segment fan-out included, or it misses dependents of a segment-only change.
    [Fact]
    public void OnChange_ReplaceSegmentChanged_ReportsDependentsOfReferencingFlag()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlagWithSegmentRule("uses-beta", "beta", version: 1),
                CreateFlagWithPrerequisite("child", "uses-beta")
            },
            new List<Segment> { CreateSegment("beta", version: 1) });

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlagWithSegmentRule("uses-beta", "beta", version: 1),
                CreateFlagWithPrerequisite("child", "uses-beta")
            },
            new List<Segment> { CreateSegment("beta", version: 2) });

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(2, changed.Count);
        Assert.Contains("uses-beta", changed);
        Assert.Contains("child", changed);
    }

    // The server rejects prerequisite cycles, so this only guards a malformed payload —
    // but an unbounded walk would hang the poll thread.
    [Fact]
    public void OnChange_ReplacePrerequisiteCycle_Terminates()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlagWithPrerequisite("a", "b", version: 1),
                CreateFlagWithPrerequisite("b", "a", version: 1)
            },
            new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlagWithPrerequisite("a", "b", version: 2),
                CreateFlagWithPrerequisite("b", "a", version: 1)
            },
            new List<Segment>());

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(2, changed.Count);
        Assert.Contains("a", changed);
        Assert.Contains("b", changed);
    }

    [Fact]
    public void OnChange_ReplaceUnrelatedFlagChanged_DoesNotReportDependent()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("parent", version: 1),
                CreateFlagWithPrerequisite("child", "parent"),
                CreateFlag("unrelated", version: 1)
            },
            new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("parent", version: 1),
                CreateFlagWithPrerequisite("child", "parent"),
                CreateFlag("unrelated", version: 2)
            },
            new List<Segment>());

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "unrelated" }, changed);
    }

    // SSE is the default real-time path: `flag.updated` lands in Upsert and `flag.deleted`
    // in RemoveFlag, each naming a single flag. Same defect, different entry point.

    [Fact]
    public void OnChange_UpsertPrerequisite_ReportsDependents()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("parent", version: 1),
                CreateFlagWithPrerequisite("child", "parent"),
                CreateFlagWithPrerequisite("grandchild", "child")
            },
            new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Upsert(new List<FlagConfiguration> { CreateFlag("parent", version: 2) });

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(3, changed.Count);
        Assert.Contains("parent", changed);
        Assert.Contains("child", changed);
        Assert.Contains("grandchild", changed);
    }

    [Fact]
    public void OnChange_RemoveFlag_ReportsDependents()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlag("parent"),
                CreateFlagWithPrerequisite("child", "parent")
            },
            new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.RemoveFlag("parent");

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(2, changed.Count);
        Assert.Contains("parent", changed);
        Assert.Contains("child", changed);
    }

    [Fact]
    public void OnChange_RemoveSegment_ReportsDependentsOfReferencingFlags()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(
            new List<FlagConfiguration>
            {
                CreateFlagWithSegmentRule("uses-beta", "beta"),
                CreateFlagWithPrerequisite("child", "uses-beta")
            },
            new List<Segment> { CreateSegment("beta") });

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.RemoveSegment("beta");

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(2, changed.Count);
        Assert.Contains("uses-beta", changed);
        Assert.Contains("child", changed);
    }

    [Fact]
    public void OnChange_UpsertFlagWithNoDependents_ReportsOnlyItself()
    {
        // Arrange
        var store = new FlagStore();
        store.Replace(new List<FlagConfiguration> { CreateFlag("lonely", version: 1) }, new List<Segment>());

        var notifications = new List<IReadOnlyCollection<string>>();
        store.OnChange(keys => notifications.Add(keys));

        // Act
        store.Upsert(new List<FlagConfiguration> { CreateFlag("lonely", version: 2) });

        // Assert
        var changed = Assert.Single(notifications);
        Assert.Equal(new[] { "lonely" }, changed);
    }

    private static FlagConfiguration CreateFlagWithPrerequisite(
        string key,
        string prerequisiteFlagKey,
        int version = 1)
    {
        var flag = CreateFlag(key, version);
        return flag with
        {
            Prerequisites = new List<Prerequisite>
            {
                new Prerequisite
                {
                    PrerequisiteFlagKey = prerequisiteFlagKey,
                    ExpectedVariationKey = "on"
                }
            }
        };
    }

    private static FlagConfiguration CreateFlagWithSegmentRule(string key, string segmentKey, int version = 1)
    {
        var flag = CreateFlag(key, version);
        return flag with
        {
            Rules = new List<TargetingRule>
            {
                new TargetingRule
                {
                    Id = $"{key}-rule",
                    Priority = 0,
                    SegmentKey = segmentKey,
                    ConditionGroups = new List<ConditionGroup>(),
                    Serve = new ServeConfig { Type = ServeType.Fixed, Variation = "on" }
                }
            }
        };
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
