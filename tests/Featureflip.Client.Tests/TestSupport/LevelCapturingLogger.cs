using Microsoft.Extensions.Logging;

namespace Featureflip.Client.Tests.TestSupport;

/// <summary>
/// Captures (level, message) pairs so a test can assert on SEVERITY, not just on
/// text. The <c>CapturingLogger</c> copies in FlagsChangedTests/InspectorTests
/// record Warnings only, which cannot express "was NOT logged at Warning" — the
/// assertion the SSE reconnect-noise guards need (#2457).
/// </summary>
internal sealed class LevelCapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    public IReadOnlyList<(LogLevel Level, string Message)> Snapshot()
    {
        lock (Entries)
        {
            return Entries.ToList();
        }
    }
}
