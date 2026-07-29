# Changelog

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
