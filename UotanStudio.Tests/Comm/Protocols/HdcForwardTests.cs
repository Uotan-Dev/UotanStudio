using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Protocols.Hdc;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public sealed class HdcForwardTests
{
    [Fact]
    public async Task Forward_UsesContextEnvelope_TransfersData_AndReleasesListener()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var port = FreePort();
        using var session = HdcTransferTests.Session(message =>
        {
            var payload = message.Payload ?? [];
            HdcCommand command;
            byte[] reply;
            switch (message.Command)
            {
                case HdcCommand.ForwardCheck:
                    Assert.Equal(new byte[] { 0, 0, 0, 1 }, payload[..4]);
                    Assert.Equal("tcp:12345\0", System.Text.Encoding.UTF8.GetString(payload[12..]));
                    command = HdcCommand.ForwardCheckResult; reply = [.. payload[..4], 0]; break;
                case HdcCommand.ForwardActiveSlave:
                    command = HdcCommand.ForwardActiveMaster; reply = payload[..4]; break;
                case HdcCommand.ForwardData:
                    command = HdcCommand.ForwardData; reply = payload; break;
                default: return [];
            }
            return [HdcDaemonCodec.Encode(new(command, message.ChannelId, 0, reply))];
        });
        session.Connection.Forwards.Add(port, 12345, cancellationToken: timeout.Token);
        Assert.Contains($"tcp:{port}", session.Connection.Forwards.List());
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await client.GetStream().WriteAsync("hello"u8.ToArray(), timeout.Token);
        var response = new byte[5];
        await client.GetStream().ReadExactlyAsync(response, timeout.Token);
        Assert.Equal("hello"u8.ToArray(), response);
        Assert.True(session.Connection.Forwards.Remove(port));
        Assert.Empty(session.Connection.Forwards.List());
        using var replacement = new TcpListener(IPAddress.Loopback, port);
        replacement.Start();
    }

    [Fact]
    public void Reverse_RejectsPeerChangingHostEndpoint()
    {
        using var session = HdcTransferTests.Session(message => message.Command == HdcCommand.ForwardInit
            ? [HdcDaemonCodec.Encode(new(HdcCommand.ForwardCheck, message.ChannelId, 0,
                [0, 0, 0, 7, 0, 0, 0, 0, 0, 0, 0, 0, .. "tcp:1\0"u8.ToArray()]))] : []);
        Assert.Throws<InvalidDataException>(() => session.Connection.Forwards.Add(12345, 12346, true, TestContext.Current.CancellationToken));
        Assert.Empty(session.Connection.Forwards.List());
    }

    [Fact]
    public async Task Reverse_ActivatesRequestedLocalEndpoint_AndPumpsBothWays()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = new byte[13 + System.Text.Encoding.UTF8.GetByteCount($"tcp:{port}")];
        BinaryPrimitives.WriteUInt32BigEndian(endpoint, 7);
        System.Text.Encoding.UTF8.GetBytes($"tcp:{port}", endpoint.AsSpan(12));
        using var session = HdcTransferTests.Session(message => message.Command switch
        {
            HdcCommand.ForwardInit => [Reply(HdcCommand.ForwardCheck, endpoint)],
            HdcCommand.ForwardCheckResult => [Reply(HdcCommand.Echo, [2, .. "OK"u8.ToArray()]), Reply(HdcCommand.ForwardActiveSlave, endpoint)],
            HdcCommand.ForwardActiveMaster => [Reply(HdcCommand.ForwardData, [0, 0, 0, 7, .. "ping"u8.ToArray()])],
            HdcCommand.ForwardData => [Reply(HdcCommand.ForwardData, message.Payload!)],
            _ => [],
        });
        session.Connection.Forwards.Add(port, 12346, true, timeout.Token);
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        var buffer = new byte[4];
        await client.GetStream().ReadExactlyAsync(buffer, timeout.Token);
        Assert.Equal("ping"u8.ToArray(), buffer);
        await client.GetStream().WriteAsync("pong"u8.ToArray(), timeout.Token);
        await client.GetStream().ReadExactlyAsync(buffer, timeout.Token);
        Assert.Equal("pong"u8.ToArray(), buffer);
        Assert.True(session.Connection.Forwards.Remove(12346, true));

        static byte[] Reply(HdcCommand command, byte[] payload) => HdcDaemonCodec.Encode(new(command, 1, 0, payload));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
