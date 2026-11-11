using Device = Turian.Engine.Core.Device;

namespace Turian.Tests;

/// <summary>Validation-layer selection and report routing without a GPU or an installed layer.</summary>
public sealed class DeviceValidationTests
{
    /// <summary>Validation stays off unless requested, and the layer is only probed when it is.</summary>
    [Fact]
    public void NotRequestedSkipsTheLayerProbe()
    {
        var logger = new RecordingLogger();

        Assert.False(Device.ResolveValidation(false, static () => throw new InvalidOperationException(), logger));
        Assert.Empty(logger.Entries);
    }

    /// <summary>A requested and installed layer runs silently.</summary>
    [Fact]
    public void RequestedAndInstalledEnablesValidation()
    {
        var logger = new RecordingLogger();

        Assert.True(Device.ResolveValidation(true, static () => true, logger));
        Assert.Empty(logger.Entries);
    }

    /// <summary>A missing layer warns and lets the game run, as a release package without one must.</summary>
    [Fact]
    public void RequestedButMissingWarnsAndContinues()
    {
        var logger = new RecordingLogger();

        Assert.False(Device.ResolveValidation(true, static () => false, logger));
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    /// <summary>Reports log at the level of their severity, and verbose or informational ones are dropped.</summary>
    [Theory]
    [InlineData(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt, LogLevel.Error)]
    [InlineData(DebugUtilsMessageSeverityFlagsEXT.WarningBitExt, LogLevel.Warning)]
    [InlineData(DebugUtilsMessageSeverityFlagsEXT.InfoBitExt, null)]
    [InlineData(DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt, null)]
    public void RoutesReportsBySeverity(DebugUtilsMessageSeverityFlagsEXT severity, LogLevel? level)
    {
        var logger = new RecordingLogger();

        Device.ReportValidationMessage(logger, severity, "VUID-example {not a template}");

        if (level is null)
        {
            Assert.Empty(logger.Entries);
            return;
        }
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(level, entry.Level);
        Assert.Equal("Vulkan validation: VUID-example {not a template}", entry.Message);
    }

    sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
