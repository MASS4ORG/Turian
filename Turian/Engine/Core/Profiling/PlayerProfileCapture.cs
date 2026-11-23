#if !TURIAN_NO_CPU_MARKERS
using System.Net.Sockets;
#endif

namespace Turian.Engine.Core;

/// <summary>Owns the development player's optional loopback session and capture history.</summary>
sealed class PlayerProfileCapture : IDisposable
{
    readonly PlayerProfileSession session;
    readonly PlayerProfileServer server;

    PlayerProfileCapture(int port, string token, string build)
    {
        session = new PlayerProfileSession(Guid.NewGuid(), Environment.ProcessId,
            $"{RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}",
            build, Stopwatch.Frequency, ["cpu", "managed-frame-counters"]);
        server = new PlayerProfileServer(port, token, Read);
    }

    /// <summary>Collects frames on the player thread.</summary>
    public PlayerProfileRecorder Recorder { get; } = new();

    /// <summary>Starts only when the build allows profiling and the launcher explicitly configures access.</summary>
    internal static PlayerProfileCapture? Start(GamePlayerContext context)
    {
#if TURIAN_NO_CPU_MARKERS
        return null;
#else
        if (!context.Development || !context.AllowPlayerProfiling)
        {
            return null;
        }
        var portText = Environment.GetEnvironmentVariable(PlayerProfileProtocol.PortVariable);
        var token = Environment.GetEnvironmentVariable(PlayerProfileProtocol.TokenVariable);
        if (portText is null && token is null)
        {
            return null;
        }
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535 ||
            token is not { Length: >= 32 and <= 128 })
        {
            context.Logger.LogWarning("Player profiler requires a valid loopback port and a token of 32–128 characters");
            return null;
        }

        TypeRegistry.TryGetType("Usercode.Game", out var game);
        var build = $"Game {GameRuntime.BuildVersion(game?.Assembly)} / "
            + $"Engine {GameRuntime.BuildVersion(typeof(GameRuntime).Assembly)}";
        try
        {
            var capture = new PlayerProfileCapture(port, token, build);
            context.Logger.LogInformation("Player profiler listening on loopback port {Port}", port);
            return capture;
        }
        catch (SocketException ex)
        {
            context.Logger.LogWarning("Unable to start the player profiler on port {Port}: {Status}", port, ex.SocketErrorCode);
            return null;
        }
#endif
    }

    internal PlayerProfileResponse Read(PlayerProfileRequest request)
    {
        if (request.Version != PlayerProfileProtocol.Version || request.AfterFrame < 0)
        {
            return new(PlayerProfileProtocol.Version, null, "Unsupported version or invalid cursor.", 0, []);
        }
        var after = request.SessionId == session.Id ? request.AfterFrame : 0L;
        var frames = Recorder.ReadAfter(after, PlayerProfileProtocol.BatchSize);
        var dropped = frames.Length > 0 ? frames[0].Number - after - 1 : 0;
        return new(PlayerProfileProtocol.Version, session, null, dropped, frames);
    }

    /// <inheritdoc />
    public void Dispose() => server.Dispose();
}
