using System.Text.Json;
using Featureflip.Client.Internal;
using Featureflip.Client.Internal.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Featureflip.Client.Tests;

public class FlagsChangedTests
{
    [Fact]
    public void Core_OnFlagsChanged_ReceivesStoreUpdates()
    {
        // Arrange
        var store = new FlagStore();
        var core = SharedFeatureflipCore.CreateForTesting(store);
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1", version: 1) }, new List<Segment>());

        var received = new List<IReadOnlyCollection<string>>();
        core.OnFlagsChanged(keys => received.Add(keys));

        // Act
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1", version: 2) }, new List<Segment>());

        // Assert
        var changed = Assert.Single(received);
        Assert.Equal(new[] { "flag-1" }, changed);
    }

    [Fact]
    public void Core_OnFlagsChanged_Unsubscribe_StopsDelivery()
    {
        // Arrange
        var store = new FlagStore();
        var core = SharedFeatureflipCore.CreateForTesting(store);
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        var received = new List<IReadOnlyCollection<string>>();
        var subscription = core.OnFlagsChanged(keys => received.Add(keys));

        // Act
        subscription.Dispose();
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        Assert.Empty(received);
    }

    [Fact]
    public void Core_OnFlagsChanged_ThrowingListener_DoesNotStopSiblings()
    {
        // Arrange
        var store = new FlagStore();
        var core = SharedFeatureflipCore.CreateForTesting(store);
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        var reached = false;
        core.OnFlagsChanged(_ => throw new InvalidOperationException("boom"));
        core.OnFlagsChanged(_ => reached = true);

        // Act
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        Assert.True(reached);
    }

    [Fact]
    public void Client_FlagsChanged_RaisedWithChangedKeys()
    {
        // Arrange
        var store = new FlagStore();
        using var client = new FeatureflipClient(store, new FlagEvaluator(), new FeatureFlagOptions());
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1", version: 1) }, new List<Segment>());

        var received = new List<IReadOnlyCollection<string>>();
        client.FlagsChanged += (_, e) => received.Add(e.ChangedKeys);

        // Act
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1", version: 2) }, new List<Segment>());

        // Assert
        var changed = Assert.Single(received);
        Assert.Equal(new[] { "flag-1" }, changed);
    }

    [Fact]
    public void Client_FlagsChanged_NotRaisedForInitialLoad()
    {
        // Arrange — a cold load is signalled by WaitForInitializationAsync, not by this event.
        var store = new FlagStore();
        using var client = new FeatureflipClient(store, new FlagEvaluator(), new FeatureFlagOptions());

        var received = new List<IReadOnlyCollection<string>>();
        client.FlagsChanged += (_, e) => received.Add(e.ChangedKeys);

        // Act
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        Assert.Empty(received);
    }

    [Fact]
    public void Client_FlagsChanged_UnsubscribedHandler_StopsReceiving()
    {
        // Arrange
        var store = new FlagStore();
        using var client = new FeatureflipClient(store, new FlagEvaluator(), new FeatureFlagOptions());
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        var count = 0;
        EventHandler<FlagsChangedEventArgs> handler = (_, _) => count++;
        client.FlagsChanged += handler;
        client.FlagsChanged -= handler;

        // Act
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void Client_FlagsChanged_NotRaisedAfterDispose()
    {
        // Arrange — a two-handle core, so disposing `first` does NOT shut the core down
        // (refcount stays at 1, held by `second`). This is deliberate: with a single-handle
        // core, Dispose() drives the refcount to 0, which shuts the core down and
        // unsubscribes it from the store entirely — at that point NotifyFlagsChanged's own
        // `if (IsShutDown) return` would suppress the notification regardless of whether
        // FeatureflipClient.Dispose tears down its own subscriptions, so a single-handle
        // version of this test can't distinguish "the handle's teardown worked" from
        // "the whole core stopped". Keeping `second` alive isolates the property this test
        // actually targets: one handle's Dispose must not leave a live subscription behind.
        var store = new FlagStore();
        var core = SharedFeatureflipCore.CreateForTesting(store);
        var first = new FeatureflipClient(core);
        // The FeatureflipClient(core) ctor does NOT acquire — its refcount contract
        // requires the caller to reserve an increment for every handle after the first.
        Assert.True(core.TryAcquire());
        using var second = new FeatureflipClient(core);
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        var firstCount = 0;
        var secondCount = 0;
        first.FlagsChanged += (_, _) => firstCount++;
        second.FlagsChanged += (_, _) => secondCount++;

        // Act
        first.Dispose();
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        Assert.Equal(0, firstCount);
        Assert.Equal(1, secondCount);
    }

    [Fact]
    public void Client_FlagsChanged_ThrowingHandler_DoesNotStopSiblings()
    {
        // Arrange
        var store = new FlagStore();
        using var client = new FeatureflipClient(store, new FlagEvaluator(), new FeatureFlagOptions());
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        var reached = false;
        client.FlagsChanged += (_, _) => throw new InvalidOperationException("boom");
        client.FlagsChanged += (_, _) => reached = true;

        // Act — must not propagate into the caller that triggered the store update.
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        Assert.True(reached);
    }

    [Fact]
    public void Client_FlagsChanged_TwoHandlesOnOneCore_BothReceive()
    {
        // Arrange — handles share a refcounted core, so both independently registered
        // handlers observe every update (neither handle's registration is dropped in
        // favor of the other's). Dispose-independence between handles is covered
        // separately by Client_FlagsChanged_NotRaisedAfterDispose.
        var store = new FlagStore();
        var core = SharedFeatureflipCore.CreateForTesting(store);
        using var first = new FeatureflipClient(core);
        // The FeatureflipClient(core) ctor does NOT acquire — its refcount contract
        // requires the caller to reserve an increment for every handle after the first.
        Assert.True(core.TryAcquire());
        using var second = new FeatureflipClient(core);
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        var firstCount = 0;
        var secondCount = 0;
        first.FlagsChanged += (_, _) => firstCount++;
        second.FlagsChanged += (_, _) => secondCount++;

        // Act
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        Assert.Equal(1, firstCount);
        Assert.Equal(1, secondCount);
    }

    [Fact]
    public void Client_FlagsChanged_ThrowingHandler_IsLoggedAtWarning()
    {
        // Arrange — the sole test defending the design rationale for giving each handler
        // its own core subscription rather than one multicast delegate behind a single
        // subscription: SharedFeatureflipCore.NotifyFlagsChanged both isolates a throwing
        // listener AND logs it at warning. A handle-level multicast invoke would have to
        // catch exceptions itself, where FeatureflipClient has no logger, so this contract
        // would silently stop holding. Without this test, a later refactor collapsing the
        // per-handler subscriptions into one field would go fully green.
        var store = new FlagStore();
        var logger = new CapturingLogger();
        var core = SharedFeatureflipCore.CreateForTesting(store, new FlagEvaluator(), new FeatureFlagOptions(), logger);
        using var client = new FeatureflipClient(core);
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        client.FlagsChanged += (_, _) => throw new InvalidOperationException("boom");

        // Act
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("listener", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Client_FlagsChanged_SameHandlerTwice_FiresOnce_ThenSingleUnsubscribeStopsIt()
    {
        // Arrange — proves the documented departure from standard multicast-delegate
        // semantics (IFeatureflipClient.FlagsChanged remarks): registering the same
        // handler delegate twice via `+=` must not double-deliver, and a single `-=`
        // must fully unregister it. Guards FeatureflipClient's `_flagsChangedSubscriptions
        // .ContainsKey(value)` check in `add` — a later refactor to a field-like
        // `public event EventHandler<...> FlagsChanged;` would leave this failing.
        var store = new FlagStore();
        using var client = new FeatureflipClient(store, new FlagEvaluator(), new FeatureFlagOptions());
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        var count = 0;
        EventHandler<FlagsChangedEventArgs> handler = (_, _) => count++;
        client.FlagsChanged += handler;
        client.FlagsChanged += handler;

        // Act
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1", version: 1) }, new List<Segment>());

        // Assert — fired once despite two `+=` registrations of the same delegate.
        Assert.Equal(1, count);

        // Act — the first (and only) `-=` unregisters it.
        client.FlagsChanged -= handler;
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1", version: 2) }, new List<Segment>());

        // Assert — no further delivery.
        Assert.Equal(1, count);
    }

    [Fact]
    public void Client_FlagsChanged_TwoHandlers_ReceiveDistinctChangedKeysInstances()
    {
        // Arrange — FlagsChangedEventArgs.ChangedKeys's backing object (a HashSet<string>
        // from FlagStore.Replace/Upsert, or a string[] from FlagStore.RemoveFlag) is one
        // instance shared by every handler and every handle for a given update. Each
        // handler must receive its own copy so one subscriber downcasting and mutating
        // its copy can't corrupt what a sibling handler observes for the same update.
        var store = new FlagStore();
        using var client = new FeatureflipClient(store, new FlagEvaluator(), new FeatureFlagOptions());
        store.Replace(new List<FlagConfiguration>(), new List<Segment>());

        IReadOnlyCollection<string>? firstKeys = null;
        IReadOnlyCollection<string>? secondKeys = null;
        client.FlagsChanged += (_, e) => firstKeys = e.ChangedKeys;
        client.FlagsChanged += (_, e) => secondKeys = e.ChangedKeys;

        // Act
        store.Replace(new List<FlagConfiguration> { CreateFlag("flag-1") }, new List<Segment>());

        // Assert
        Assert.NotNull(firstKeys);
        Assert.NotNull(secondKeys);
        Assert.NotSame(firstKeys, secondKeys);
        Assert.Equal(firstKeys, secondKeys);
    }

    [Fact]
    public void Client_FlagsChanged_SubscribeAfterDispose_DoesNotLeakCoreListener()
    {
        // Arrange — two-handle core so the core itself stays alive after `first.Dispose()`
        // (see Client_FlagsChanged_NotRaisedAfterDispose for why a single-handle core
        // can't isolate this). Asserts on the core's raw listener-array size rather than
        // on notification behavior: the `_disposed` guard in FeatureflipClient's `add`
        // already suppresses delivery to a post-Dispose subscriber, so a behavior-only
        // assertion would pass even if that `+=` allocated a fresh subscription
        // dictionary and registered a new core listener that nothing will ever tear
        // down — rooting the disposed handle and its delegate on the core's
        // copy-on-write array for the core's remaining lifetime.
        var store = new FlagStore();
        var core = SharedFeatureflipCore.CreateForTesting(store);
        var first = new FeatureflipClient(core);
        // The FeatureflipClient(core) ctor does NOT acquire — its refcount contract
        // requires the caller to reserve an increment for every handle after the first.
        Assert.True(core.TryAcquire());
        using var second = new FeatureflipClient(core);

        // Act
        first.Dispose();
        first.FlagsChanged += (_, _) => { };

        // Assert — the post-Dispose `+=` registered no new core subscription.
        Assert.Equal(0, core.FlagsChangedListenerCount);
    }

    [Fact]
    public void Client_FlagsChanged_Dispose_ReleasesCoreSubscription()
    {
        // Arrange — asserts on the core's raw listener-array size rather than on
        // notification behavior. The `_disposed` guard added for the in-flight-fan-out
        // race (see Client_FlagsChanged_NotRaisedAfterDispose) makes any behavior-based
        // assertion pass even if Dispose never actually removes the listener from the
        // core: the guard alone would suppress the callback and mask a leaked entry
        // sitting forever in SharedFeatureflipCore's copy-on-write array (rooting the
        // disposed handle and its handler delegate for the core's remaining lifetime).
        // Only inspecting the array size directly can catch that leak.
        var store = new FlagStore();
        var core = SharedFeatureflipCore.CreateForTesting(store);
        var first = new FeatureflipClient(core);
        // The FeatureflipClient(core) ctor does NOT acquire — its refcount contract
        // requires the caller to reserve an increment for every handle after the first.
        Assert.True(core.TryAcquire());
        using var second = new FeatureflipClient(core);

        first.FlagsChanged += (_, _) => { };
        Assert.Equal(1, core.FlagsChangedListenerCount);

        // Act
        first.Dispose();

        // Assert — Dispose released the handle's core subscription, not merely silenced it.
        Assert.Equal(0, core.FlagsChangedListenerCount);
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

    // Mirrors InspectorTests.CapturingLogger (that one is private to its own class, so
    // not reusable from here) — captures Warning-level log messages for assertion.
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
