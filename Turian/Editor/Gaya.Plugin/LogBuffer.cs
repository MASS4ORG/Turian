namespace Gaya.Plugin.Turian;

/// <summary>A ring buffer of recent log events, fed by an <see cref="ILoggerProvider"/> and read by the Output panel.</summary>
public static class LogBuffer
{
    const int capacity = 2000;
    static readonly ConcurrentQueue<LogLine> Lines = new();

    /// <summary>Notifies listeners when log contents change, on the thread that wrote or cleared them.</summary>
    public static event Action? Changed;

    /// <summary>The provider to add with <c>builder.AddProvider(LogBuffer.Provider)</c>.</summary>
    public static ILoggerProvider Provider { get; } = new BufferLoggerProvider();

    /// <summary>A snapshot of the buffered lines, oldest first.</summary>
    public static IReadOnlyList<LogLine> Snapshot() => [.. Lines];

    /// <summary>Drops every buffered line, so the Output panel's Clear button empties the console.</summary>
    public static void Clear()
    {
        while (Lines.TryDequeue(out _)) { }
        Changed?.Invoke();
    }

    static void Add(LogLine line)
    {
        Lines.Enqueue(line);
        while (Lines.Count > capacity && Lines.TryDequeue(out _)) { }
        Changed?.Invoke();
    }

    sealed class BufferLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new BufferLogger(categoryName);

        public void Dispose()
        {
        }
    }

    sealed class BufferLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (exception is not null) message = $"{message}{Environment.NewLine}{exception}";
            Add(LogOrigin.Capture(logLevel, message, category));
        }
    }
}
