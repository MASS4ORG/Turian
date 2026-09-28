namespace Turian.Engine.Core;

/// <summary>
/// Provides functionality for lap logging with various timing details.
/// </summary>
/// <remarks>
/// Every line is logged as a single pre-formatted <c>{Line}</c> argument rather than with one
/// placeholder per column: <c>Microsoft.Extensions.Logging</c>'s message template parser has no
/// equivalent of Serilog's <c>{Name,alignment}</c> column padding, so the columns are padded here
/// with ordinary composite-format alignment before the line ever reaches the logger.
/// </remarks>
public static class LapLogger
{
    static long _firstTick;

    static long _lastTick;

    static readonly Dictionary<string, long> Laps = [];

    /// <summary>
    /// Restart the timer and initialize the logger information.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public static void RestartTimer(this ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _firstTick = DateTime.Now.Ticks;
        _lastTick = _firstTick;
        logger.LogInformation("{Line}",
            $" {"Memory",8} | {"Total",14} | {"Last",12} | {"Lap",12} | {"Name",-12} | Message");
        logger.LogInformation("{Line}",
            "--------------------------------------------------------------------------------------");
    }

    /// <summary>
    /// Start a new lap with the specified name.
    /// </summary>
    /// <param name="lap">The name of the lap.</param>
    public static void StartLap(string lap) => Laps[lap] = DateTime.Now.Ticks;

    /// <summary>
    /// Log the lap information.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="lap">The name of the lap.</param>
    /// <param name="msg">The message to log.</param>
    /// <param name="skipTimer">Whether to skip the timer or not.</param>
    public static void Lap(this ILogger? logger, string lap, string msg, bool skipTimer = false)
    {
        if (logger is null)
        {
            return;
        }

        var memory = Process.GetCurrentProcess().WorkingSet64 / 1_000_000;
        var now = DateTime.Now.Ticks;
        if (!skipTimer)
        {
            if (!Laps.TryGetValue(lap, out var value))
            {
                value = now;
                Laps[lap] = value;
            }
            var lapTicks = T2Ms(now - value);
            logger.LogInformation("{Line}",
                $"{memory,6} MB | {T2Ms(now - _firstTick),14} | {T2Ms(now - _lastTick),12} | {lapTicks,12} | {lap,12} | {msg}");
            _lastTick = now;
        }
        else
        {
            logger.LogInformation("{Line}",
                $"{memory,6} MB | {"",14} | {"",12} | {"",12} | {"",12} | {msg}");
        }
    }

    /// <summary>
    /// Convert ticks to milliseconds string.
    /// </summary>
    /// <param name="tick">The tick value to convert.</param>
    /// <returns>The milliseconds representation in string.</returns>
    static string T2Ms(long tick)
    {
        return $"{tick / 10_000d:#,##0.000}ms";
    }
}
