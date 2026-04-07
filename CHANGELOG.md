# Changelog

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
