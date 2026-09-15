using System.Text;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public sealed class AdbIdleConnectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadTimeout_OnlyFaultsAnIncompletePacket(bool partialPacket)
    {
        using var session = new AdbSession(new FakeAdbDeviceTransport(), AdbAuthentication.CreateNew());
        session.Connect();
        using var stream = session.Connection.OpenStream("localabstract:scrcpy_test");
        var transport = (FakeAdbDeviceTransport)session.Transport;
        var packet = AdbMessaging.Serialize(new AdbMessage(AdbCommand.Wrte, 1, stream.LocalId, "resumed"u8.ToArray()));
        if (partialPacket)
        {
            transport.Enqueue(packet[..7]);
            // The header read stalls past the 5000 ms transport read timeout; only this branch
            // needs to wait for the idle timeout to fault the incomplete packet.
            // 只有该分支需要等待空闲超时使不完整报文故障；非不完整分支无需等待。
            await Task.Delay(6000, TestContext.Current.CancellationToken);
            Assert.False(session.IsConnected);
            Assert.True(stream.IsFaulted);
            Assert.IsType<TimeoutException>(stream.Fault);
        }
        else
        {
            transport.Enqueue(packet);
            Assert.True(session.IsConnected);
            Assert.Equal("resumed", Encoding.UTF8.GetString(Assert.IsType<byte[]>(stream.Read(2000))));
            using var next = session.Connection.OpenStream("shell:echo next");
            Assert.False(next.IsFaulted);
        }
    }
}