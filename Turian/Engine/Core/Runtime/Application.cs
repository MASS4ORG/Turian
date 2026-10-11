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
