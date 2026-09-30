using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Hdc;

public sealed class HdcDaemonTests
{
    [Fact]
    public void DevicePacket_MatchesIndependentProtobufWireFixture()
    {
        var expected = Convert.FromHexString("4857000001000900000002080110E907180020096964");
        Assert.Equal(expected, HdcDaemonCodec.Encode(new(HdcCommand.UnityExecute, 1, 99, "id"u8.ToArray())));
        var message = HdcDaemonCodec.Decode(expected);
        Assert.Equal(HdcCommand.UnityExecute, message.Command);
        Assert.Equal("id", message.PayloadAsString());
        expected[19] = 8;
        Assert.Throws<InvalidDataException>(() => HdcDaemonCodec.Decode(expected));
    }

    [Fact]
    public void Handshake_UsesAuthStateAndStringVersion()
    {
        var expected = Convert.FromHexString("0A084F484F53204844431000180122002A00320131");
        Assert.Equal(expected, HdcDaemonCodec.Handshake(0, 1, "", [], "1"));
        var handshake = HdcDaemonCodec.ReadHandshake(expected);
        Assert.Equal((byte)0, handshake.State);
        Assert.Equal("1", handshake.Version);
    }

    [Fact]
    public void Session_WaitsForAuthOk_ExecutesWithoutChannelHandshake_ReceivesRawEcho()
    {
        using var transport = new FakeCommTransport();
        var authenticationClosed = false;
        transport.OnWrite = packet =>
        {
            var message = HdcDaemonCodec.Decode(packet);
            if (message.Command == HdcCommand.Handshake)
            {
                var hello = HdcDaemonCodec.ReadHandshake(message.Payload!);
                Assert.Equal((byte)0, hello.State);
                return [HdcDaemonCodec.Encode(new(HdcCommand.Handshake, 0, 0,
                    HdcDaemonCodec.Handshake(4, hello.SessionId, "", [], "test"))),
                    HdcDaemonCodec.Encode(new(HdcCommand.ChannelClose, 0, 0, [1]))];
            }
            if (message.Command == HdcCommand.ChannelClose && message.ChannelId == 0)
            {
                Assert.Equal([0], message.Payload);
                authenticationClosed = true;
            }
            if (message.Command == HdcCommand.UnityExecute)
            {
                Assert.True(authenticationClosed);
                Assert.Equal("id", message.PayloadAsString());
                return [Convert.FromHexString("48570000010008000000020801100A180020096F6B"),
                    Convert.FromHexString("4857000001000800000001080110021800200900")];
            }
            return [];
        };
        using var session = new HdcSession(transport, HdcTestKeys.CreateAuthentication());
        session.Connect();
        Assert.True(authenticationClosed);
        var result = session.ExecuteCommand("id", 2000);
        Assert.Equal("ok", Encoding.UTF8.GetString(result.Stdout));
        Assert.Equal(-1, result.ExitCode);
    }

    [Theory]
    [InlineData("4857000001FFFF00000000")]
    [InlineData("48570000010008FFFFFFFF")]
    [InlineData("4857010001000800000000")]
    public void InvalidHeaders_AreRejectedBeforeBodyAllocation(string hex)
        => Assert.Throws<InvalidDataException>(() => HdcDaemonCodec.ReadHeader(Convert.FromHexString(hex)));

    [Theory]
    [InlineData(11u, false)]
    [InlineData(524299u, false)]
    [InlineData(524300u, true)]
    [InlineData(4294967295u, true)]
    public void UsbBlockLimit_CoversHeaderMetadataAndPayloadCeiling(uint dataSize, bool rejected)
        => Assert.Equal(rejected, HdcConnection.ExceedsUsbBlockLimit(dataSize));

    [Fact]
    public void StaleBlockDrain_ConsumesPendingBlocks_ReturnsWhenTransportIdles()
    {
        using var transport = new FakeCommTransport();
        transport.Enqueue(new byte[4096]);
        transport.Enqueue(new byte[17]);
        HdcConnection.DrainStaleBlocks(transport, TestContext.Current.CancellationToken);
        Assert.Equal(0, transport.Read(new byte[64], 0, 64, 0));
    }

    [Fact]
    public void StaleBlockDrain_LeavesPendingBlocksUnconsumed_AfterCancellation()
    {
        using var transport = new FakeCommTransport();
        transport.Enqueue(new byte[64]);
        transport.Enqueue(new byte[128]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => HdcConnection.DrainStaleBlocks(transport, cancelled.Token));
        Assert.Equal(64, transport.Read(new byte[256], 0, 256, 0));
    }
}