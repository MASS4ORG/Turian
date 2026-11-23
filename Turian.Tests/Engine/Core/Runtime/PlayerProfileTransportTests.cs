using System.Net;
using System.Net.Sockets;

namespace Turian.Tests;

/// <summary>Checks loopback authentication and bounded profile requests.</summary>
public sealed class PlayerProfileTransportTests
{
    /// <summary>Authenticated reads return the snapshot selected by their cursor.</summary>
    [Fact]
    public async Task ReadAsyncReturnsCallbackSnapshotForAuthenticatedLoopbackRequest()
    {
        var sessionId = Guid.NewGuid();
        var request = new PlayerProfileRequest(1, "secret", sessionId, 12);
        var expected = new PlayerProfileResponse(1,
            new PlayerProfileSession(sessionId, 123, "test", "build", 60, []), null, 0, []);
        using var server = new PlayerProfileServer(0, "secret", actual =>
        {
            Assert.Equal(request, actual);
            return expected;
        });

        var actual = await PlayerProfileClient.ReadAsync(server.Port, request, TestContext.Current.CancellationToken);

        Assert.Equal(expected.Session?.Id, actual.Session?.Id);
        Assert.Equal(expected.Session?.ProcessId, actual.Session?.ProcessId);
        Assert.Null(actual.Error);
    }

    /// <summary>Missing and incorrect credentials cannot reach the reader.</summary>
    [Theory]
    [InlineData("wrong")]
    [InlineData(null)]
    public async Task ReadAsyncRejectsIncorrectTokenWithoutCallingReader(string? token)
    {
        using var server = new PlayerProfileServer(0, "secret", static _ =>
            throw new InvalidOperationException("Unauthorized request reached the reader."));

        var response = await PlayerProfileClient.ReadAsync(server.Port,
            new PlayerProfileRequest(1, token!, Guid.NewGuid(), 0), TestContext.Current.CancellationToken);

        Assert.Equal("Unauthorized", response.Error);
    }

    /// <summary>A bad length closes its connection and the listener still serves subsequent requests.</summary>
    [Fact]
    public async Task ServerClosesConnectionWhenFrameLengthExceedsBound()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var server = new PlayerProfileServer(0, "secret", static _ =>
            throw new InvalidOperationException("Malformed request reached the reader."));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        await using var stream = client.GetStream();
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, PlayerProfileServer.MaximumRequestLength + 1);
        await stream.WriteAsync(header, timeout.Token);

        var buffer = new byte[1];
        await Assert.ThrowsAsync<EndOfStreamException>(() => stream.ReadExactlyAsync(buffer, timeout.Token).AsTask());
        var next = await PlayerProfileClient.ReadAsync(server.Port,
            new PlayerProfileRequest(PlayerProfileProtocol.Version, "wrong", Guid.Empty, 0), timeout.Token);
        Assert.Equal("Unauthorized", next.Error);
    }
}
