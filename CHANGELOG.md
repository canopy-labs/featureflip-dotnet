# Changelog

## 2.6.0 — 2026-07-29

### Added

- **`FlagsChanged` event.** `IFeatureflipClient` now raises `FlagsChanged` with the keys of flags whose configuration changed, so consumers can react to updates without polling. Batched (one raise per update), never raised for the initial flag load, and silent for unchanged poll ticks — `FlagStore` diffs each snapshot and resolves changed segments back to the flags whose rules reference them (#2080).
- **Prerequisites resolve back to their dependents.** Toggling a flag that another lists as a prerequisite changes what the dependent evaluates to, but bumps only the prerequisite's own version — so dependents were silently absent from `ChangedKeys` and a consumer caching per key served them stale. Every notification path now fans out transitively through prerequisites: full snapshots (poll/`sync`) and the SSE deltas (`flag.created`/`flag.updated`/`flag.deleted`) alike. Cycle-safe (#2087).

- **A variation carrying no usable value no longer breaks snapshot updates.** `Variation.Value` is a non-nullable `JsonElement`, which System.Text.Json leaves at `ValueKind.Undefined` for both an absent `value` property and an explicit `"value": null` — and a variation holding JSON null is supported config. Re-serializing such an element throws, so the content comparison below would have failed every full snapshot and frozen the store on its last good config. The comparison writes `Undefined` as null instead (#2088).
- **Full-snapshot change detection compares configuration, not `version`.** The wire version is deliberately second-granular — eval-api divides its internal epoch-millisecond version by 1000 to keep the public contract 32-bit safe for published SDKs — so it cannot distinguish two edits to one flag inside the same wall-clock second. On a poll, SSE `sync`, or `segment.updated` refetch there is no per-edit signal to fall back on, so if a snapshot boundary fell between those two edits the newer config landed in the store while the diff reported nothing changed. `FlagStore` now compares each flag's and segment's serialized configuration, which also covers any field added to a config model without the comparison having to be extended by hand. Unchanged poll ticks stay silent as before (#2088).
- **An SSE flag delta always raises `FlagsChanged`, with no version comparison.** The wire version is second-granular, so two edits to one flag inside the same wall-clock second carry an identical version — and the store applied the second edit's config while suppressing its notification, leaving a consumer that caches per key on the pre-edit value. Only `flag.created`/`flag.updated` reach this path and the server emits those only for a real change, so the event is the signal. Matches `@featureflip/js` (#2090).

### Changed

- **`IFeatureflipClient` gained a member.** Adding `FlagsChanged` is source-breaking for external implementors of the interface; netstandard2.0 support rules out a default interface method.

## 2.5.0 — 2026-07-29

### Added

- **`onEvaluation` inspector callback.** `Inspectors` config option registering in-process observers fired on every evaluation (#1914).

### Fixed

- An explicit `"flags": null` in a response payload no longer hangs initialization. `GetFlagsResponse` normalizes in the `init` accessor, so the throw can no longer land after the store update but before the initialization signal — which left the client stuck in `WaitForInitializationAsync` and re-failing every poll (#1934).
- The inspector event's `VariationKey`/`RuleId`/`PrerequisiteKey` are nulled when the served variation key is undefined on the flag (#1987).

## 2.4.0 — 2026-07-14

### Added

- `WaitForInitializationAsync` for OpenFeature init parity (#1924).

## 2.3.0 — 2026-07-13

### Fixed

- Outage-recovery hardening: SSE `sync` applied as a full store replace, plus clean-EOF backoff (#1876, #1896).

## 2.2.1 — 2026-06-23

### Fixed

- The SSE stream gets its own `HttpClient` with an infinite timeout. The shared client's 10s `Timeout` applied to the long-lived stream, killing it every 10 seconds (#1526).

## 2.2.0 — 2026-06-19

### Added

- **Semantic-version condition operators** (`SemverEquals`, `SemverGreaterThan`, `SemverGreaterThanOrEqual`, `SemverLessThan`, `SemverLessThanOrEqual`) for local rule evaluation. Versions are compared per [semver.org](https://semver.org) precedence rather than as decimals, so multi-segment versions like `2.10.1` target correctly (the numeric operators silently mis-targeted them). Mirrors the evaluation engine and the JS SDK (#1409 follow-up, #1430).

### Fixed

- Per-flag rollout salt aligns bucketing with the engine and every other SDK; the previous `flagKey` fallback re-bucketed users (#1452).
- Semver prerelease comparison is case-sensitive in ASCII order per semver §11 (#1447).
- `MatchesRegex` is case-sensitive — the pattern is no longer compiled with `RegexOptions.IgnoreCase` (#1453).
- Numeric operators return no-match on non-numeric operands instead of a lexical `string.Compare` fallback (#1456).
- `Before`/`After` date operators aligned with the engine (#1455).
- Type-aware numeric coercion for `Equals`/`In` (#1458).
- Keyless rollouts serve the control variation deterministically (#1457).
- Environment-level percentage rollouts with no variations no longer throw (#1469).

## 2.1.0 — 2026-05-27

### Added

- **Prerequisite flag support** (#1107).

## 2.0.0 — 2026-04-06

### BREAKING

- **Public `FeatureflipClient` constructor removed.** The only way to obtain a client is now the static factory `FeatureflipClient.Get(sdkKey, options, logger)`. The factory dedupes by SDK key: repeated calls with the same key return handles pointing at a single shared underlying client, making scoped/transient DI misregistration harmless instead of leaking SSE connections and background tasks.

  **Migration:**

  Before:
  ```csharp
  var client = new FeatureflipClient("your-sdk-key");
  ```

  After:
  ```csharp
  var client = FeatureflipClient.Get("your-sdk-key");
  ```

  Users of the `Featureflip.Client.Extensions.DependencyInjection` package need no code changes — `AddFeatureflip` now routes through the factory internally and continues to work unchanged.

- **Disposal is now refcounted.** When multiple handles share one cached core, disposing one handle does not shut down the core — the underlying background tasks and SSE connection stay alive until the last handle is disposed. This is a behavior change from the previous `Dispose()` semantics, which immediately released resources.

### Added

- `FeatureflipClient.Get(sdkKey, options, logger)` — static factory, the new primary entry point.
- Internal `SharedFeatureflipCore` class separating expensive resources from the public handle.

### Changed

- `FeatureflipClient` is now a thin handle over `SharedFeatureflipCore`. All evaluation, flush, and dispose operations delegate to the core.
- `Featureflip.Client.Extensions.DependencyInjection.AddFeatureflip` overloads now route through `FeatureflipClient.Get()` internally. No public API changes.

## 1.0.1

Previous release.

## 1.0.0

Initial stable release.

## 0.1.0

Initial release.
