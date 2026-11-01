using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// A logger that keeps every entry it is given, formatted, with its structured values.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.ToDictionary(p => p.Key, p => p.Value)
            : new Dictionary<string, object?>();
        _entries.Enqueue(new Entry(logLevel, formatter(state, exception), values, exception));
    }

    internal sealed record Entry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values, Exception? Exception);
}
