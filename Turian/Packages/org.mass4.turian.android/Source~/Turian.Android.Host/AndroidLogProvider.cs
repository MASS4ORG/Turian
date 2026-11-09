using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Turian.Android;

/// <summary>Android log priorities as defined by <c>android/log.h</c>.</summary>
public enum AndroidLogPriority
{
    /// <summary>Most detailed tracing.</summary>
    Verbose = 2,

    /// <summary>Diagnostic detail for development.</summary>
    Debug = 3,

    /// <summary>Normal operational messages.</summary>
    Info = 4,

    /// <summary>Recoverable problems.</summary>
    Warn = 5,

    /// <summary>Failures of an operation.</summary>
    Error = 6,

    /// <summary>Failures that end the application.</summary>
    Fatal = 7,
}

/// <summary>Writes engine logs to logcat under one tag, readable with <c>adb logcat -s Turian</c>.</summary>
/// <param name="minimumLevel">The least severe level written; lower levels are dropped before formatting.</param>
/// <param name="tag">The logcat tag every entry carries.</param>
/// <param name="write">The sink receiving each entry; the native Android log by default.</param>
public sealed partial class AndroidLogProvider(
    LogLevel minimumLevel,
    string tag = AndroidLogProvider.DefaultTag,
    Action<AndroidLogPriority, string, string>? write = null) : ILoggerProvider
{
    /// <summary>The tag engine entries use in logcat.</summary>
    public const string DefaultTag = "Turian";

    /// <summary>The largest UTF-8 payload written per entry; logcat truncates longer ones.</summary>
    internal const int MaxEntryBytes = 4000;

    readonly LogLevel minimumLevel = minimumLevel;
    readonly string tag = tag;
    readonly Action<AndroidLogPriority, string, string> write = write ?? WriteNative;

    /// <summary>The level a launcher uses: debug detail in development builds, information otherwise.</summary>
    /// <param name="development">Whether the build is a development build.</param>
    /// <returns>The minimum level to log.</returns>
    public static LogLevel LevelFor(bool development) => development ? LogLevel.Debug : LogLevel.Information;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new AndroidLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    internal static AndroidLogPriority PriorityFor(LogLevel level) => level switch
    {
        LogLevel.Trace => AndroidLogPriority.Verbose,
        LogLevel.Debug => AndroidLogPriority.Debug,
        LogLevel.Information => AndroidLogPriority.Info,
        LogLevel.Warning => AndroidLogPriority.Warn,
        LogLevel.Error => AndroidLogPriority.Error,
        _ => AndroidLogPriority.Fatal,
    };

    /// <summary>Splits text into logcat-sized pieces, preferring line breaks so stack frames stay whole.</summary>
    internal static IEnumerable<string> Split(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= MaxEntryBytes)
        {
            yield return text;
            yield break;
        }
        var chunk = new StringBuilder();
        var chunkBytes = 0;
        foreach (var line in text.Split('\n'))
        {
            var bytes = Encoding.UTF8.GetByteCount(line);
            if (chunk.Length > 0 && chunkBytes + 1 + bytes > MaxEntryBytes)
            {
                yield return chunk.ToString();
                chunk.Clear();
                chunkBytes = 0;
            }
            if (bytes > MaxEntryBytes)
            {
                foreach (var piece in SplitLine(line))
                {
                    yield return piece;
                }
                continue;
            }
            if (chunk.Length > 0)
            {
                chunk.Append('\n');
                chunkBytes++;
            }
            chunk.Append(line);
            chunkBytes += bytes;
        }
        if (chunk.Length > 0)
        {
            yield return chunk.ToString();
        }
    }

    /// <summary>Cuts one line into the longest pieces that fit an entry, never inside a surrogate pair.</summary>
    static IEnumerable<string> SplitLine(string line)
    {
        var start = 0;
        while (start < line.Length)
        {
            var end = start;
            var bytes = 0;
            while (end < line.Length)
            {
                var pair = char.IsSurrogatePair(line, end);
                var width = pair ? 4 : line[end] < 0x80 ? 1 : line[end] < 0x800 ? 2 : 3;
                if (bytes + width > MaxEntryBytes)
                {
                    break;
                }
                bytes += width;
                end += pair ? 2 : 1;
            }
            yield return line[start..end];
            start = end;
        }
    }

    static void WriteNative(AndroidLogPriority priority, string tag, string text) =>
        _ = AndroidLogWrite((int)priority, tag, text);

    /// <summary>Writes one entry to the native Android log.</summary>
    [LibraryImport("log", EntryPoint = "__android_log_write", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int AndroidLogWrite(int priority, string tag, string text);

    sealed class AndroidLogger(AndroidLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider.minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var message = formatter(state, exception);
            var text = category == provider.tag ? message : $"{category}: {message}";
            if (exception is not null)
            {
                text = $"{text}\n{exception}";
            }
            var priority = PriorityFor(logLevel);
            foreach (var piece in Split(text))
            {
                provider.write(priority, provider.tag, piece);
            }
        }
    }
}
