using Turian.Android;

namespace Turian.Tests;

/// <summary>Logcat entry filtering, priorities, formatting and size limits without an Android device.</summary>
public sealed class AndroidLogProviderTests
{
    readonly List<(AndroidLogPriority Priority, string Tag, string Text)> entries = [];

    AndroidLogProvider Create(LogLevel minimumLevel) =>
        new(minimumLevel, write: (priority, tag, text) => entries.Add((priority, tag, text)));

    /// <summary>Development builds log debug detail; other builds start at information.</summary>
    [Fact]
    public void LevelFollowsDevelopmentFlag()
    {
        Assert.Equal(LogLevel.Debug, AndroidLogProvider.LevelFor(development: true));
        Assert.Equal(LogLevel.Information, AndroidLogProvider.LevelFor(development: false));
    }

    /// <summary>Entries below the minimum level and None are dropped before reaching logcat.</summary>
    [Fact]
    public void DropsEntriesBelowMinimumLevel()
    {
        using var provider = Create(LogLevel.Information);
        var logger = provider.CreateLogger(AndroidLogProvider.DefaultTag);

        logger.LogDebug("hidden");
        logger.LogInformation("shown");

        Assert.False(logger.IsEnabled(LogLevel.None));
        Assert.Equal([(AndroidLogPriority.Info, "Turian", "shown")], entries);
    }

    /// <summary>Every .NET level maps to its Android priority.</summary>
    [Theory]
    [InlineData(LogLevel.Trace, AndroidLogPriority.Verbose)]
    [InlineData(LogLevel.Debug, AndroidLogPriority.Debug)]
    [InlineData(LogLevel.Information, AndroidLogPriority.Info)]
    [InlineData(LogLevel.Warning, AndroidLogPriority.Warn)]
    [InlineData(LogLevel.Error, AndroidLogPriority.Error)]
    [InlineData(LogLevel.Critical, AndroidLogPriority.Fatal)]
    public void MapsLevelsToPriorities(LogLevel level, AndroidLogPriority priority) =>
        Assert.Equal(priority, AndroidLogProvider.PriorityFor(level));

    /// <summary>Other categories are prefixed and exceptions follow the message under the engine tag.</summary>
    [Fact]
    public void FormatsCategoryAndException()
    {
        using var provider = Create(LogLevel.Trace);
        var exception = new InvalidOperationException("boom");

        provider.CreateLogger("Renderer").LogError(exception, "frame {Index} failed", 3);

        var entry = Assert.Single(entries);
        Assert.Equal(AndroidLogPriority.Error, entry.Priority);
        Assert.Equal("Turian", entry.Tag);
        Assert.Equal($"Renderer: frame 3 failed\n{exception}", entry.Text);
    }

    /// <summary>Long text is split at line breaks into entries logcat does not truncate, keeping all content.</summary>
    [Fact]
    public void SplitsLongTextAtLineBreaks()
    {
        var lines = Enumerable.Range(0, 200).Select(static index => $"   at Frame{index}(Argument value)").ToArray();
        var text = string.Join('\n', lines);

        var pieces = AndroidLogProvider.Split(text).ToArray();

        Assert.True(pieces.Length > 1);
        Assert.All(pieces, static piece => Assert.True(Encoding.UTF8.GetByteCount(piece) <= AndroidLogProvider.MaxEntryBytes));
        Assert.Equal(lines, pieces.SelectMany(static piece => piece.Split('\n')));
    }

    /// <summary>A single oversized line is cut without separating a surrogate pair.</summary>
    [Fact]
    public void SplitsOversizedLineWithoutBreakingSurrogates()
    {
        var text = string.Concat(Enumerable.Repeat("a😀", 3000));

        var pieces = AndroidLogProvider.Split(text).ToArray();

        Assert.True(pieces.Length > 1);
        Assert.All(pieces, static piece =>
        {
            Assert.True(Encoding.UTF8.GetByteCount(piece) <= AndroidLogProvider.MaxEntryBytes);
            Assert.False(char.IsHighSurrogate(piece[^1]));
        });
        Assert.Equal(text, string.Concat(pieces));
    }

    /// <summary>An oversized line between short ones gets its own entries and surrounding lines stay intact.</summary>
    [Fact]
    public void IsolatesOversizedLineBetweenShortLines()
    {
        var oversized = new string('x', AndroidLogProvider.MaxEntryBytes + 10);

        var pieces = AndroidLogProvider.Split($"before\n{oversized}\nafter").ToArray();

        Assert.Equal(["before", oversized[..AndroidLogProvider.MaxEntryBytes], "xxxxxxxxxx", "after"], pieces);
    }

    /// <summary>Short text, including an empty message, is written as one entry unchanged.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("ready")]
    public void KeepsShortTextWhole(string text) => Assert.Equal([text], AndroidLogProvider.Split(text));
}
