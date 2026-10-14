namespace Turian.Engine.Core;

/// <summary>
/// Plays a game with no window, renderer or input device: a dedicated server, or a build without a render brick.
/// Ticks the scenes at the project's fixed simulation rate until <see cref="Application.Quit"/>, Ctrl+C or SIGTERM.
/// </summary>
public sealed class HeadlessPlayer : IGamePlayer
{
    /// <inheritdoc />
    public int Run(GamePlayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Application.BeginSession(headless: true);

        using var interrupt = Application.QuitOnInterrupt();
        var runtime = GameRuntime.Start(context, new BufferedInputSource());
        using var asyncSession = runtime.Ticker.Async;
        runtime.LoadStartupScene();
        context.Logger.LogInformation("{Product} running headless at {Rate:F0} ticks per second",
            runtime.ProductName, 1 / runtime.Ticker.Clock.FixedDeltaTime);
        Loop(runtime.Ticker, Application.QuitToken);
        return Application.ExitCode;
    }

    /// <summary>Ticks once per fixed interval, measured on a monotonic clock, until <paramref name="quit"/>.</summary>
    internal static void Loop(SceneTicker ticker, CancellationToken quit)
    {
        var interval = TimeSpan.FromSeconds(ticker.Clock.FixedDeltaTime);
        var clock = Stopwatch.StartNew();
        var previous = TimeSpan.Zero;
        while (!quit.IsCancellationRequested)
        {
            var now = clock.Elapsed;
            ticker.Tick((now - previous).TotalSeconds);
            previous = now;

            var wait = interval - (clock.Elapsed - now);
            if (wait > TimeSpan.Zero)
            {
                quit.WaitHandle.WaitOne(wait);
            }
        }
    }
}
