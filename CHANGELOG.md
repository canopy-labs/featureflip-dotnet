# Changelog

## 2.10.0 — 2026-10-20

### Added

- Every request now sends `User-Agent: featureflip-csharp/<version>`, on the flag fetch, event post and SSE stream alike. The SDK previously sent no User-Agent at all, so .NET usage could not be told apart from anything else reaching the Evaluation API. The version comes from the assembly's informational version, which the release tag sets, so it cannot go stale between releases. (#4139)
- `FeatureFlagOptions.WrapperName` and `WrapperVersion`, for libraries that wrap this SDK (an OpenFeature provider, an internal platform client). When set, the wrapper's token follows the SDK's in the User-Agent, e.g. `featureflip-csharp/<version> my-wrapper/1.0.0`. A name that is not a valid HTTP product token is ignored with a warning rather than failing construction. (#4139)

## 2.9.0 — 2026-08-26

### Changed

- A `Before`/`After` date operand must now be ISO-8601 or a Unix timestamp in seconds. This SDK made the same `DateTimeOffset.TryParse` call the evaluation engine makes, and so inherited its leniency about the date **format** rather than merely about ISO-8601: `05/15/2023`, `5/15/2023`, `2024/01/01`, `Jan 1 2024`, `January 1, 2024`, `2024.01.01`, `2024-1-1` and `Mon, 15 May 2023 00:00:00 GMT` all resolved here and matched nothing in the go, java, python, ruby, php and js SDKs. A C# customer therefore got a different targeting result than a Go one from the same saved flag config. All seven SDKs now share one grammar. (#2480)
- Date operands are now case-sensitive: `2024-01-31t09:30:00z` matches nothing where `2024-01-31T09:30:00Z` matches. The shared grammar is case-sensitive in every SDK. (#2480)
- A decimal on the minutes position (`2024-01-31T09:30.5`) is now read as a fraction of seconds and matches, where `DateTimeOffset.TryParse` rejected it outright. This is the one shape where the SDKs agree with each other *against* the engine. (#2480)

The **evaluation engine deliberately keeps** its leniency, because narrowing it would stop an operand that evaluates today from evaluating at all, for rules customers may already have saved. The Management API now rejects a non-ISO date operand on write, so a rule saved from this release onward cannot carry one. If you have an existing rule using one of these formats, rewrite the operand as ISO-8601 (`2023-05-15`, `2023-05-15T09:30:00Z`, `2023-05-15T09:30:00+05:00`) or as a Unix timestamp in seconds.

- A long-lived SSE stream severed mid-frame is now logged at `Debug` instead of `Warning`. Such a sever is routine behind a CDN or reverse proxy: the client reconnects and the server replays a full `sync` snapshot, so no configuration is missed and nothing is degraded — but reporting it at `Warning` turned ordinary operation into a recurring alarm. A stream that ends *without* ever delivering a snapshot carries no configuration, and is now reported by the core at `Warning`, so a genuinely broken stream stays visible. (#2457)

### Fixed

- An explicit `FlushAsync` no longer opens a second drain loop while one is already running. The in-flight latch added for #2456 guarded only the batch-size trigger, so the periodic flush, an explicit `FlushAsync` and a size-triggered flush could enter the loop together — two request streams against an endpoint the backoff gate exists to protect, and worse, a success in one cleared the gate a failure in the other had just armed, re-opening the one-request-per-evaluation behaviour outright. A caller arriving while a drain is running now waits for it and returns, matching the js and node SDKs. Shutdown still bypasses coalescing, because it is the last drain there will ever be. (#2477)

- The first SSE reconnect after a healthy stream drops is now jittered to `[d/2, d]`, like every other backoff level. The drops this absorbs are fleet-wide — a single edge event severs every stream at once — so every client re-entered the backoff together and waited an identical delay, republishing the drop's own synchronisation as a reconnect spike one backoff later. Measured in production: a drop spread across 2.5–3.0 ms produced a reconnect spread of 26–46 ms. The delay never exceeds the previous one and stays strictly positive, so a stream that fails immediately still cannot busy-loop. (#2508)

## 2.8.1 — 2026-08-24

### Fixed

- A date operand is now trimmed of exactly the whitespace the evaluation engine trims (tab, newline, vertical tab, form feed, carriage return and space), and is rejected outright if it still carries a NUL, another control character, or a non-ASCII whitespace character. Each SDK had been relying on its own language's `trim`, and no two of those cover the same set, so the same operand could match on one SDK and match nothing on another. (#2468)

## 2.8.0 — 2026-08-24

### Fixed

- Analytics events now survive a transient failure of the events endpoint. The queue is drained before the batch is sent, so until now any non-2xx or network error discarded that batch outright — the public edge answers this endpoint with a 503 at a low but constant rate, so evaluation analytics were being lost steadily. A retryable failure (5xx, 429, transport fault, timeout) returns the batch to the front of the queue for the next flush; a permanent one (401/403/400) still drops it, because retrying a rejected SDK key forever would starve every later event. (#2456)

### Changed

- The event queue is now bounded at 10,000 events. It is only reachable during a sustained outage of the events endpoint, where the oldest events are shed first so memory stays bounded and the freshest analytics are kept.
- A flush sends one request per batch instead of one for the whole queue. Re-queuing is what lets the queue reach its bound during an outage, and posting a backlog of that size in a single request risks a body the server rejects with a 413 — which is non-retryable, so the path meant to preserve the backlog would be the one that discarded it.
- A batch-size-triggered flush now backs off while the endpoint is failing. A re-queued batch leaves the queue at or above the batch size, so the size trigger would otherwise fire on every recorded event and turn a failing endpoint into one request per evaluation. The periodic flush is the retry vehicle instead.

## 2.7.1 — 2026-08-23

### Fixed

- An unrecognised `FlagType` no longer fails the entire config fetch. One unknown value in one flag discarded the whole payload, so a single new server-side type could blank out every flag the SDK served. (#2401)
- An unrecognised condition operator now fails closed instead of matching every user. The default arm returned `false`, which a negated condition then inverted to `true` — so a config naming an operator the SDK did not know could silently target everyone. (#2262)
- An unrecognised condition operator is tolerated at deserialization instead of discarding the whole config, matching the string-typed SDKs. (#2372)
- Integer values are rejected where an enum is expected, instead of being coerced into a variant the wire format never named. (#2315)

## 2.7.0 — 2026-08-20

### Changed

- A type-mismatched read returns the caller's default and reports `EvaluationReason.Error`, instead of returning it under the evaluator's *success* reason, which left callers no signal at all. Reading a String flag through a number accessor, say, is now detectable rather than silent. Matching reads and the generic/JSON accessors are unchanged. (#2281)
### Fixed

- A closed handle serves the caller's default from every accessor and reports not-initialized. `Dispose()` releases the shared core — stopping streaming and polling, shutting down the event processor — but the in-memory store stayed readable, so a closed client kept evaluating against a frozen snapshot that could never update again while still reporting itself initialized. (#2309)

- A null `EvaluationContext` no longer throws an unguarded `NullReferenceException` out of an evaluation that had already succeeded. `Evaluate<T>` tolerates a null context — it is wrapped so nothing escapes, and the inspector payload substitutes an empty context — but `TrackEvaluation` runs after it, outside that protection, and dereferenced the context. Because tracking is called unconditionally, *every* `Variation`/`VariationDetail` call with a null context threw, missing flags included. A null context is now an anonymous evaluation: the event is still recorded, with the user id left null. (#2311)

## 2.6.1 — 2026-08-05

### Fixed

- `LICENSE` is now the verbatim Apache-2.0 text. Three phrases in the operative sections had been reworded and the appendix dropped, which left automated license scanners unable to identify it. The license itself is unchanged; the file now says what it always claimed to.
- The README's License section said MIT. `LICENSE`, the `.csproj` and the NuGet listing have always said Apache-2.0, which is the actual license.
- The XML documentation on `FlagsChangedEventArgs.ChangedKeys` described a coalescing gap that no longer exists. Change detection compares configuration content, not the second-granular wire `Version`, so two edits inside one second are distinguished; what remains is ordinary snapshot coalescing, which is not time-bounded. The behaviour was already correct — only the remark shipped in IntelliSense was stale.

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
  ``csharp
  var client = new FeatureflipClient("your-sdk-key");
  ``

  After:
  ``csharp
  var client = FeatureflipClient.Get("your-sdk-key");
  ``

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
