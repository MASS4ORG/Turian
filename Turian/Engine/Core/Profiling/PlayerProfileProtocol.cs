namespace Turian.Engine.Core;

/// <summary>Versioning and opt-in settings for the read-only development player profiler.</summary>
public static class PlayerProfileProtocol
{
    /// <summary>Version of the profiler request and response format.</summary>
    public const int Version = 1;

    /// <summary>Environment variable for the IPv4 loopback listener port.</summary>
    public const string PortVariable = "TURIAN_PROFILE_PORT";

    /// <summary>Environment variable for the shared access token.</summary>
    public const string TokenVariable = "TURIAN_PROFILE_TOKEN";

    /// <summary>Maximum number of frames in one response.</summary>
    public const int BatchSize = 8;
}

/// <summary>Requests retained frames after a cursor in one capture session.</summary>
/// <param name="Version">Supported client protocol version.</param>
/// <param name="Token">Shared access token configured by the launcher.</param>
/// <param name="SessionId">Previous session ID, or an empty ID to start reading.</param>
/// <param name="AfterFrame">Last received frame number.</param>
public sealed record PlayerProfileRequest(int Version, string Token, Guid SessionId, long AfterFrame);

/// <summary>A bounded batch of completed player frames or a protocol error.</summary>
/// <param name="Version">Server protocol version.</param>
/// <param name="Session">Current session metadata on success.</param>
/// <param name="Error">Protocol error on failure.</param>
/// <param name="DroppedFrames">Requested frames that have expired from the player history.</param>
/// <param name="Frames">Completed frames in ascending order.</param>
public sealed record PlayerProfileResponse(int Version, PlayerProfileSession? Session, string? Error,
    long DroppedFrames, PlayerProfileFrame[] Frames);

/// <summary>Describes a player session and the clock used by its frames.</summary>
/// <param name="Id">Unique ID for this player capture.</param>
/// <param name="ProcessId">Operating system process ID.</param>
/// <param name="Device">Platform and process architecture.</param>
/// <param name="Build">Game and engine informational versions.</param>
/// <param name="ClockFrequency">Player Stopwatch ticks per second.</param>
/// <param name="Capabilities">Supported capture streams.</param>
public sealed record PlayerProfileSession(Guid Id, int ProcessId, string Device, string Build,
    long ClockFrequency, string[] Capabilities);

/// <summary>A completed simulation frame and its CPU samples. Treat the samples as immutable.</summary>
/// <param name="Number">Monotonically increasing number within the player session.</param>
/// <param name="StartTimestamp">Start on the player Stopwatch clock.</param>
/// <param name="DurationTicks">Elapsed time in player Stopwatch ticks.</param>
/// <param name="DroppedSamples">Samples omitted because the frame buffer was full.</param>
/// <param name="UnfinishedSamples">Samples still open at frame completion.</param>
/// <param name="AllocatedBytes">Managed allocation on the frame thread.</param>
/// <param name="ManagedBytes">Estimated live managed bytes across the process.</param>
/// <param name="Gen0Collections">Process generation zero collections during the frame.</param>
/// <param name="Gen1Collections">Process generation one collections during the frame.</param>
/// <param name="Gen2Collections">Process generation two collections during the frame.</param>
/// <param name="Samples">Samples in parent-first recording order.</param>
public sealed record PlayerProfileFrame(long Number, long StartTimestamp, long DurationTicks, int DroppedSamples,
    int UnfinishedSamples, long AllocatedBytes, long ManagedBytes, int Gen0Collections, int Gen1Collections,
    int Gen2Collections, PlayerProfileSample[] Samples);

/// <summary>A named CPU timing interval relative to its player frame.</summary>
/// <param name="Name">Sample label.</param>
/// <param name="ParentIndex">Index of the parent sample, or minus one.</param>
/// <param name="StartTicks">Start relative to the frame.</param>
/// <param name="DurationTicks">Elapsed time in player Stopwatch ticks.</param>
public readonly record struct PlayerProfileSample(string Name, int ParentIndex, long StartTicks, long DurationTicks);

[JsonSerializable(typeof(PlayerProfileRequest))]
[JsonSerializable(typeof(PlayerProfileResponse))]
sealed partial class PlayerProfileJsonContext : JsonSerializerContext;
