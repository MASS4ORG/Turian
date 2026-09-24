using Microsoft.Build.Framework;

namespace Turian.Editor.Core;

/// <summary>
/// Custom logger for MSBuild that forwards logs to <see cref="Microsoft.Extensions.Logging.ILogger"/>.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MsBuildForwardingLogger"/> class.
/// </remarks>
/// <param name="logger">The logger instance to forward the MSBuild logs to.</param>
public class MsBuildForwardingLogger(Microsoft.Extensions.Logging.ILogger logger) : Microsoft.Build.Framework.ILogger
{
    readonly Microsoft.Extensions.Logging.ILogger logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Gets or sets the level of verbosity at which to log.
    /// </summary>
    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Normal;

    /// <summary>
    /// Gets or sets the logger parameters.
    /// </summary>
    public string? Parameters
    {
        get => field;
        set { }
    } = string.Empty;

    /// <summary>
    /// Initializes the logger with the specified event source.
    /// </summary>
    /// <param name="eventSource">The event source to attach to.</param>
    public void Initialize(IEventSource eventSource)
    {
        if (eventSource == null)
        {
            throw new ArgumentNullException(nameof(eventSource));
        }

        eventSource.ErrorRaised += (_, e) =>
            {
                if (e == null)
                {
                    return;
                }
                logger.LogError("{File}({LineNumber},{ColumnNumber}): error {Code}: {Message}",
                    e.File ?? "N/A", e.LineNumber, e.ColumnNumber, e.Code, e.Message);
            };
        eventSource.WarningRaised += (_, e) =>
            {
                if (e == null)
                {
                    return;
                }
                logger.LogWarning("{File}({LineNumber},{ColumnNumber}): warning {Code}: {Message}",
                    e.File ?? "N/A", e.LineNumber, e.ColumnNumber, e.Code, e.Message);
            };
        eventSource.MessageRaised += (_, e) =>
            {
                if (e == null || string.IsNullOrEmpty(e.Message))
                {
                    return;
                }
                logger.Log(ToLogLevel(e.Importance), "{Message}", e.Message);
            };
    }

    /// <summary>
    /// Performs any finalization necessary before the logger is closed.
    /// </summary>
    public void Shutdown() { }

    static LogLevel ToLogLevel(MessageImportance importance) => importance switch
    {
        MessageImportance.High => LogLevel.Information,
        MessageImportance.Normal => LogLevel.Debug,
        _ => LogLevel.Trace,
    };
}
