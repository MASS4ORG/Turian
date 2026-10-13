namespace Turian.Engine.Core;

/// <summary>Process-level control of a running game, as Unity's <c>Application</c>.</summary>
public static class Application
{
    static CancellationTokenSource quit = new();

    /// <summary>
    /// Whether the game runs without a window, renderer or input device: a dedicated server, or a client build
    /// started where no renderer is installed.
    /// </summary>
    public static bool IsHeadless { get; private set; }

    /// <summary>Signalled once the game is asked to quit; players end their loop on it.</summary>
    public static CancellationToken QuitToken => quit.Token;

    /// <summary>The process exit code the last <see cref="Quit"/> asked for.</summary>
    public static int ExitCode { get; private set; }

    /// <summary>
    /// Quits the game on Ctrl+C or SIGTERM, as a console application does; a second interrupt while the game is
    /// already quitting ends the process, for a game that hangs. Dispose the result to stop listening; the players
    /// register this for the span of their loop.
    /// </summary>
    /// <returns>A handle that unregisters the interrupt handlers when disposed.</returns>
    public static IDisposable QuitOnInterrupt()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminate);
        return new InterruptRegistration(terminate);
    }

    static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) => e.Cancel = Interrupt();

    static void OnTerminate(PosixSignalContext context) => context.Cancel = Interrupt();

    /// <summary>Handles one interrupt: the first asks the game to quit, a later one lets the process end.</summary>
    /// <returns>Whether to cancel the default termination.</returns>
    internal static bool Interrupt()
    {
        if (quit.IsCancellationRequested) return false;

        Quit();
        return true;
    }

    /// <summary>Removes the interrupt handlers <see cref="QuitOnInterrupt"/> installed.</summary>
    sealed class InterruptRegistration(PosixSignalRegistration terminate) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose()
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            terminate.Dispose();
        }
    }

    /// <summary>
    /// Asks the player to end its loop after the current frame. The editor's Play Mode has no player loop, so
    /// the request does nothing there, as in Unity.
    /// </summary>
    /// <param name="exitCode">The process exit code.</param>
    public static void Quit(int exitCode = 0)
    {
        ExitCode = exitCode;
        quit.Cancel();
    }

    /// <summary>Starts a player session: clears an earlier quit request and records how the game runs.</summary>
    /// <param name="headless">Whether the player has no window, renderer or input device.</param>
    public static void BeginSession(bool headless)
    {
        if (quit.IsCancellationRequested)
        {
            quit.Dispose();
            quit = new CancellationTokenSource();
        }

        ExitCode = 0;
        IsHeadless = headless;
    }
}
