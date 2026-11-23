using System.Net;
using System.Net.Sockets;

namespace Turian.Engine.Core;

/// <summary>Serves bounded, authenticated player profile reads over IPv4 loopback.</summary>
sealed class PlayerProfileServer : IDisposable
{
    const int MaximumPayloadLength = 1_048_576;
    internal const int MaximumRequestLength = 4096;
    static readonly TimeSpan ConnectionDeadline = TimeSpan.FromSeconds(5);
    readonly TcpListener listener;
    readonly byte[] tokenHash;
    readonly Func<PlayerProfileRequest, PlayerProfileResponse> read;
    readonly CancellationTokenSource cancellation = new();
    readonly Task acceptLoop;
    int disposed;

    /// <summary>Starts a serial loopback listener; port zero selects an available port for tests.</summary>
    public PlayerProfileServer(int port, string token, Func<PlayerProfileRequest, PlayerProfileResponse> read)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(read);
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        this.read = read;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(backlog: 4);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = AcceptLoopAsync();
    }

    /// <summary>Gets the bound loopback port.</summary>
    public int Port { get; }

    /// <summary>Stops accepting connections and releases the listener.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        cancellation.Cancel();
        listener.Stop();
        try
        {
            acceptLoop.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        cancellation.Dispose();
    }

    async Task AcceptLoopAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }
                throw;
            }

            using (client)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
            {
                deadline.CancelAfter(ConnectionDeadline);
                try
                {
                    await HandleConnectionAsync(client, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException
                    or SocketException or JsonException)
                {
                    // A malformed or abandoned connection must not stop the serial listener.
                }
            }
        }
    }

    async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        var payload = await ReadFrameAsync(stream, cancellationToken, MaximumRequestLength).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize(payload, PlayerProfileJsonContext.Default.PlayerProfileRequest);
        PlayerProfileResponse response;
        if (request is null || !HasValidToken(request.Token))
        {
            response = new PlayerProfileResponse(PlayerProfileProtocol.Version, null, "Unauthorized", 0, []);
        }
        else
        {
            try
            {
                response = read(request);
            }
            catch (Exception)
            {
                response = new PlayerProfileResponse(PlayerProfileProtocol.Version, null, "Profile read failed", 0, []);
            }
        }

        var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, PlayerProfileJsonContext.Default.PlayerProfileResponse);
        if (responseBytes.Length > MaximumPayloadLength)
        {
            responseBytes = JsonSerializer.SerializeToUtf8Bytes(
                new PlayerProfileResponse(PlayerProfileProtocol.Version, null, "Response too large", 0, []),
                PlayerProfileJsonContext.Default.PlayerProfileResponse);
        }
        await WriteFrameAsync(stream, responseBytes, cancellationToken).ConfigureAwait(false);
    }

    bool HasValidToken(string? candidate)
    {
        return candidate is { Length: > 0 and <= 128 } &&
            CryptographicOperations.FixedTimeEquals(tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));
    }

    internal static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken,
        int maximumLength = MaximumPayloadLength)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maximumLength)
        {
            throw new InvalidDataException("Invalid player profile payload length.");
        }
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    internal static async Task WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 or > MaximumPayloadLength)
        {
            throw new InvalidDataException("Invalid player profile payload length.");
        }
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Reads one player profile snapshot from a local runtime endpoint.</summary>
public static class PlayerProfileClient
{
    static readonly TimeSpan ConnectionDeadline = TimeSpan.FromSeconds(5);

    /// <summary>Requests a read-only profile snapshot from the specified IPv4 loopback port.</summary>
    public static async Task<PlayerProfileResponse> ReadAsync(int port, PlayerProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ConnectionDeadline);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token).ConfigureAwait(false);
        await using var stream = client.GetStream();
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, PlayerProfileJsonContext.Default.PlayerProfileRequest);
        if (payload.Length > PlayerProfileServer.MaximumRequestLength)
        {
            throw new InvalidDataException("Player profile request is too large.");
        }
        await PlayerProfileServer.WriteFrameAsync(stream, payload, deadline.Token).ConfigureAwait(false);
        var responsePayload = await PlayerProfileServer.ReadFrameAsync(stream, deadline.Token).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize(responsePayload, PlayerProfileJsonContext.Default.PlayerProfileResponse)
            ?? throw new InvalidDataException("Empty player profile response.");
        ValidateResponse(response, request);
        return response;
    }

    static void ValidateResponse(PlayerProfileResponse response, PlayerProfileRequest request)
    {
        if (response.DroppedFrames < 0 || response.Frames is null ||
            response.Frames.Length > PlayerProfileProtocol.BatchSize)
        {
            throw new InvalidDataException("Invalid player profile batch.");
        }
        if (response.Error is not null)
        {
            if (response.Session is null && response.Frames.Length == 0)
            {
                return;
            }
            throw new InvalidDataException("Invalid player profile error response.");
        }
        if (response.Version != request.Version ||
            response.Session is not { ClockFrequency: > 0, Device: not null, Build: not null, Capabilities: not null } session ||
            session.Id == Guid.Empty)
        {
            throw new InvalidDataException("Unsupported player profile session.");
        }

        var previous = session.Id == request.SessionId ? request.AfterFrame : 0;
        foreach (var frame in response.Frames)
        {
            if (frame is null || frame.Number <= previous || frame.StartTimestamp < 0 ||
                frame.DurationTicks < 0 || frame.StartTimestamp > long.MaxValue - frame.DurationTicks ||
                frame.Samples is null || frame.Samples.Length > 64)
            {
                throw new InvalidDataException("Invalid player profile frame.");
            }
            ValidateSamples(frame);
            previous = frame.Number;
        }
    }

    static void ValidateSamples(PlayerProfileFrame frame)
    {
        for (var i = 0; i < frame.Samples.Length; i++)
        {
            var sample = frame.Samples[i];
            if (sample.Name is not { Length: > 0 and <= 96 } || sample.ParentIndex < -1 || sample.ParentIndex >= i ||
                sample.StartTicks < 0 || sample.StartTicks > frame.DurationTicks ||
                sample.DurationTicks < 0 || sample.DurationTicks > frame.DurationTicks - sample.StartTicks)
            {
                throw new InvalidDataException("Invalid player profile sample.");
            }
        }
    }
}
