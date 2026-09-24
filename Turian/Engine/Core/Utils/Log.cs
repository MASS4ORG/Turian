using Microsoft.Extensions.Logging.Abstractions;

namespace Turian.Engine.Core;

/// <summary>
/// Ambient logging entry point for code with no route to dependency injection — static contexts
/// like <see cref="LapLogger"/>'s startup timing or the handful of places that ran before a service
/// provider exists. Everything else should take <see cref="ILogger"/> or <see cref="ILogger{T}"/>
/// through its constructor instead of reaching for this.
/// </summary>
/// <remarks>
/// Engine.Core depends only on <c>Microsoft.Extensions.Logging.Abstractions</c>, never on a
/// concrete backend, so a composition root (Studio, the editor CLI, or a built game's host) must
/// call <see cref="Configure"/> once at startup. Until then, <see cref="Logger"/> is a no-op —
/// tests and headless fixtures that never configure logging get a harmless default rather than a
/// null reference.
/// </remarks>
public static class Log
{
    static ILoggerFactory factory = NullLoggerFactory.Instance;

    /// <summary>The ambient logger. A no-op until <see cref="Configure"/> runs.</summary>
    public static ILogger Logger { get; private set; } = NullLogger.Instance;

    /// <summary>
    /// Wires the ambient logger to a real backend. Called once, at startup, by whichever
    /// composition root owns the process (Studio, the editor CLI, or the built game's host).
    /// </summary>
    /// <param name="loggerFactory">The factory the host's logging backend was built with.</param>
    public static void Configure(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        factory = loggerFactory;
        Logger = factory.CreateLogger("Turian");
    }

    /// <summary>Flushes and disposes the configured backend. A no-op if never configured.</summary>
    public static void Shutdown() => factory.Dispose();
}
