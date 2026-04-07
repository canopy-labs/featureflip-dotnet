# Featureflip C# SDK

C# SDK for [Featureflip](https://featureflip.io) - evaluate feature flags locally with near-zero latency.

## Installation

```bash
dotnet add package Featureflip.Client
```

## Quick Start

```csharp
using Featureflip.Client;

var client = FeatureflipClient.Get("your-sdk-key");

> **Lifetime:** The client is designed to be used as a singleton. Calling `FeatureflipClient.Get()` multiple times with the same SDK key returns handles sharing one underlying client — you cannot accidentally open duplicate streaming connections. For dependency injection, `AddFeatureflip` registers it as a singleton by default.

var enabled = client.BoolVariation("my-feature",
    new EvaluationContext { UserId = "user-123" }, false);

if (enabled)
{
    Console.WriteLine("Feature is enabled!");
}

client.Dispose();
```

## Configuration

```csharp
var client = FeatureflipClient.Get(
    "your-sdk-key",
    new FeatureFlagOptions
    {
        BaseUrl = "https://eval.featureflip.io",  // Evaluation API URL (default)
        Streaming = true,                          // SSE for real-time updates (default)
        PollInterval = TimeSpan.FromSeconds(30),   // Polling interval if streaming=false
        FlushInterval = TimeSpan.FromSeconds(30),  // Event flush interval
        FlushBatchSize = 100,                      // Events per batch
        InitTimeout = TimeSpan.FromSeconds(10),    // Max wait for initialization
        ConnectTimeout = TimeSpan.FromSeconds(5),  // HTTP connection timeout
        ReadTimeout = TimeSpan.FromSeconds(10),    // HTTP read timeout
        WaitForInitialization = false,             // Block constructor until ready
        StartOffline = false,                      // Continue even if init fails
    }
);
```

The SDK key can also be set via the `FEATUREFLIP_SDK_KEY` environment variable.

## Evaluation

```csharp
var context = new EvaluationContext { UserId = "123" };

// Boolean flag
bool enabled = client.BoolVariation("feature-key", context, false);

// String flag
string tier = client.StringVariation("pricing-tier", context, "free");

// Integer flag
int limit = client.IntVariation("rate-limit", context, 100);

// Double flag
double ratio = client.DoubleVariation("rollout-ratio", context, 0.5);

// JSON flag
var config = client.JsonVariation("ui-config", context, new UiConfig { Theme = "light" });

// Generic
var value = client.Variation("feature-key", context, defaultValue);
```

### Detailed Evaluation

```csharp
var detail = client.VariationDetail("feature-key",
    new EvaluationContext { UserId = "123" }, false);

Console.WriteLine(detail.Value);        // The evaluated value
Console.WriteLine(detail.Reason);       // RuleMatch, Fallthrough, FlagDisabled, etc.
Console.WriteLine(detail.RuleId);       // Rule ID if reason is RuleMatch
Console.WriteLine(detail.ErrorMessage); // Error details if reason is Error
```

## Event Flushing

```csharp
// Force flush pending events
client.Flush();

// Or async
await client.FlushAsync();
```

## Dependency Injection

```csharp
// In Startup.cs or Program.cs
services.AddFeatureflipClient("your-sdk-key");

// Inject via constructor
public class MyService
{
    private readonly IFeatureflipClient _client;

    public MyService(IFeatureflipClient client)
    {
        _client = client;
    }
}
```

## Testing

The `IFeatureflipClient` interface makes mocking straightforward:

```csharp
var mock = new Mock<IFeatureflipClient>();
mock.Setup(c => c.BoolVariation("my-feature", It.IsAny<EvaluationContext>(), false))
    .Returns(true);
```

## Features

- **Local evaluation** - Near-zero latency after initialization
- **Real-time updates** - SSE streaming with automatic polling fallback
- **Event tracking** - Automatic batching and background flushing
- **DI support** - `AddFeatureflipClient()` extension method
- **Interface-based** - `IFeatureflipClient` for easy mocking
- **Multi-target** - Supports .NET 8.0 and .NET Standard 2.0

## Requirements

- .NET 8.0+ or .NET Standard 2.0 compatible runtime

## License

MIT
