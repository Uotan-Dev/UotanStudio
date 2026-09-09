using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public sealed class HdcDaemonTests
{
    [Fact]
    public void DevicePacket_MatchesIndependentProtobufWireFixture()
    {
        // session.h: HW,reserve[2],version,BE16 metadata length,BE32 data length.
        // serial_struct.h: 08 channel,10 command(varint 1001),18 checksum,20 vcode.
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
        transport.OnWrite = packet =>
        {
            var message = HdcDaemonCodec.Decode(packet);
            if (message.Command == HdcCommand.Handshake)
            {
                var hello = HdcDaemonCodec.ReadHandshake(message.Payload!);
                Assert.Equal((byte)0, hello.State);
                return [HdcDaemonCodec.Encode(new(HdcCommand.Handshake, 0, 0,
                    HdcDaemonCodec.Handshake(4, hello.SessionId, "", [], "test")))];
            }
            if (message.Command == HdcCommand.UnityExecute)
            {
                Assert.Equal("id", message.PayloadAsString());
                return [Convert.FromHexString("48570000010008000000020801100A180020096F6B"),
                    Convert.FromHexString("4857000001000800000001080110021800200900")];
            }
            return [];
        };
        using var session = new HdcSession(transport, new HdcAuthentication(RSA.Create(3072)));
        session.Connect();
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
}
