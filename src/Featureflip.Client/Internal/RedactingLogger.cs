using System.Collections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Featureflip.Client.Internal;

/// <summary>
/// Strips the SDK key out of everything the SDK logs.
/// </summary>
/// <remarks>
/// The SDK sends the key as a request header, and the HTTP stack (or a handler the host
/// installed) may quote it in an exception message. Applied as a decorator around whatever
/// logger the SDK is given, at core construction, so every current and future log call is
/// covered and so is the host's logger, which is where a leaked key ends up.
/// </remarks>
internal sealed class RedactingLogger : ILogger
{
    internal const string Replacement = "[redacted]";

    /// <summary>
    /// Below this the value is not treated as a credential: a real key is always 54
    /// characters, and redacting a short string corrupts every line it appears inside.
    /// </summary>
    internal const int MinRedactableLength = 20;

    private readonly ILogger _inner;
    private readonly string[] _secrets;

    private RedactingLogger(ILogger inner, string[] secrets)
    {
        _inner = inner;
        _secrets = secrets;
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> once. A short key returns it unchanged; a logger already
    /// wrapped for another key is re-wrapped around the same inner logger with both keys, never nested.
    /// </summary>
    internal static ILogger Wrap(ILogger? inner, string? secret)
    {
        inner ??= NullLogger.Instance;
        if (secret is null || secret.Length < MinRedactableLength) return inner;
        if (inner is RedactingLogger existing)
        {
            if (Array.IndexOf(existing._secrets, secret) >= 0) return inner;
            var all = new string[existing._secrets.Length + 1];
            existing._secrets.CopyTo(all, 0);
            all[all.Length - 1] = secret;
            return new RedactingLogger(existing._inner, all);
        }
        return new RedactingLogger(inner, new[] { secret });
    }

    /// <summary>
    /// For an exception handed to the host: returns it untouched (by identity) when nothing in its
    /// chain mentions the key, otherwise a redacted stand-in. Fails closed if rendering throws.
    /// </summary>
    internal static Exception? Sanitize(Exception? exception, string? secret)
    {
        if (exception is null || secret is null || secret.Length < MinRedactableLength) return exception;
        return new RedactingLogger(NullLogger.Instance, new[] { secret }).RedactException(exception);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        if (state is string s) return _inner.BeginScope(Redact(s));
        return _inner.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!_inner.IsEnabled(logLevel)) return;

        var safeException = RedactException(exception);

        if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
        {
            var copy = RedactList(list);
            if (copy is null)
            {
                // Nothing in the fields mentions the key; the rendered message still might.
                _inner.Log(logLevel, eventId, state, safeException,
                    (st, _) => Redact(formatter(st, exception)));
                return;
            }
            var redacted = new RedactedState(copy, () => Redact(formatter(state, exception)));
            _inner.Log(logLevel, eventId, redacted, safeException, static (st, _) => st.Message());
            return;
        }

        _inner.Log(logLevel, eventId, state, safeException,
            (st, _) => Redact(formatter(st, exception)));
    }

    private bool Has(string text)
    {
        foreach (var secret in _secrets)
            if (text.IndexOf(secret, StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    private string Redact(string text)
    {
        foreach (var secret in _secrets)
            if (text.IndexOf(secret, StringComparison.Ordinal) >= 0) text = text.Replace(secret, Replacement);
        return text;
    }

    /// <summary>Returns null when no key, value or rendering mentions the secret (caller keeps the original).</summary>
    private List<KeyValuePair<string, object?>>? RedactList(IReadOnlyList<KeyValuePair<string, object?>> list)
    {
        List<KeyValuePair<string, object?>>? copy = null;
        for (var i = 0; i < list.Count; i++)
        {
            var kv = list[i];
            var key = kv.Key;
            var value = kv.Value;
            var newKey = key is not null && Has(key) ? Redact(key) : key;
            object? newValue = value;
            if (value is string s)
            {
                if (Has(s)) newValue = Redact(s);
            }
            else if (value is not null && !value.GetType().IsPrimitive && !value.GetType().IsEnum)
            {
                string? text;
                try { text = value.ToString(); } catch { text = null; }
                if (text is not null && Has(text)) newValue = Redact(text);
            }

            if (copy is null && (!ReferenceEquals(newKey, key) || !ReferenceEquals(newValue, value)))
            {
                copy = new List<KeyValuePair<string, object?>>(list.Count);
                for (var j = 0; j < i; j++) copy.Add(list[j]);
            }
            copy?.Add(new KeyValuePair<string, object?>(newKey!, newValue));
        }
        return copy;
    }

    private Exception? RedactException(Exception? exception)
    {
        if (exception is null) return null;
        var typeName = exception.GetType().FullName ?? "Exception";
        string text;
        try { text = exception.ToString(); }
        catch { return new RedactedException(typeName, typeName, typeName); } // fail closed
        if (!Has(text)) return exception;
        string message;
        try { message = Redact(exception.Message); } catch { message = string.Empty; }
        return new RedactedException(typeName, Redact(text), message);
    }

    /// <summary>Structured state whose values are redacted; renders through a redacted message.</summary>
    private sealed class RedactedState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly List<KeyValuePair<string, object?>> _items;
        private readonly Func<string>? _message;

        public RedactedState(List<KeyValuePair<string, object?>> items, Func<string>? message)
        {
            _items = items;
            _message = message;
        }

        public string Message() => _message?.Invoke() ?? ToString();
        public int Count => _items.Count;
        public KeyValuePair<string, object?> this[int index] => _items[index];
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => _message?.Invoke() ?? string.Empty;
    }

    /// <summary>Stands in for an exception whose text contained the key.</summary>
    internal sealed class RedactedException : Exception
    {
        private readonly string _text;

        public RedactedException(string originalType, string redactedText, string redactedMessage)
            : base($"{originalType}: {redactedMessage}")
        {
            _text = redactedText;
        }

        public override string? StackTrace => null;
        public override string ToString() => _text;
    }
}
