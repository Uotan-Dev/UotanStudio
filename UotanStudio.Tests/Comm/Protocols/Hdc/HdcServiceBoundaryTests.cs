using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Hdc;

public sealed class HdcServiceBoundaryTests
{
    [Theory]
    [InlineData("Get parameter \"const.debuggable\" fail! errNum is:1002!", true)]
    [InlineData("0\n", false)]
    public void GetProp_DistinguishesDeniedQueryFromValue(string response, bool denied)
    {
        using var session = HdcTransferTests.Session(message => message.Command == HdcCommand.UnityExecute
            ? [Reply(message, HdcCommand.EchoRaw, Encoding.UTF8.GetBytes(response)), Reply(message, HdcCommand.ChannelClose, [0])]
            : []);
        var services = new HdcDeviceServices(session.Connection);
        if (denied) Assert.Contains("1002", Assert.Throws<IOException>(() => services.GetProp("const.debuggable")).Message);
        else Assert.Equal("0", services.GetProp("const.debuggable"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrivilegedCommand_DenialIsNotSuccess(bool root)
    {
        using var session = HdcTransferTests.Session(message => message.Command is HdcCommand.UnityRootRun or HdcCommand.UnityRemount
            ? [Reply(message, HdcCommand.Echo, [0, .. "Operate need running as root"u8.ToArray()])]
            : message.Command == HdcCommand.JdwpList ? [Reply(message, HdcCommand.ChannelClose, [0])] : []);
        var services = new HdcDeviceServices(session.Connection);
        var error = Assert.Throws<IOException>(() => { if (root) services.SetRootMode(true); else services.Remount(); });
        Assert.Contains("running as root", error.ToString());
        Assert.Equal("", services.ListDebugProcesses());
    }

    [Fact]
    public void Bugreport_InvalidDestinationAndPreCancellationDoNotOpenTask()
    {
        var opened = false;
        using var session = HdcTransferTests.Session(_ => { opened = true; return []; });
        var services = new HdcDeviceServices(session.Connection);
        using var readOnly = new MemoryStream([1], writable: false);
        Assert.Throws<ArgumentException>(() => services.Bugreport(readOnly, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => services.Bugreport(Stream.Null, cancelled.Token));
        Assert.False(opened);
    }

    [Fact]
    public async Task ServiceDeadline_IsNotRenewedByProgressOutput()
    {
        using var transport = new FakeCommTransport();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task? producer = null;
        transport.OnWrite = packet =>
        {
            var message = HdcDaemonCodec.Decode(packet);
            if (message.Command == HdcCommand.Handshake)
                return [Reply(message, HdcCommand.Handshake, HdcDaemonCodec.Handshake(4, 0, "", [], "test")),
                    Reply(message, HdcCommand.ChannelClose, [1])];
            if (message.Command == HdcCommand.JdwpList)
            {
                producer = Task.Run(async () =>
                {
                    try
                    {
                        for (var i = 0; i < 50; i++)
                        {
                            transport.Enqueue(Reply(message, HdcCommand.EchoRaw, "progress"u8.ToArray()));
                            await Task.Delay(40, stop.Token);
                        }
                        transport.Enqueue(Reply(message, HdcCommand.ChannelClose, [0]));
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                });
            }
            return [];
        };
        using var session = new HdcSession(transport, HdcTestKeys.CreateAuthentication());
        session.Connect();
        try
        {
            var elapsed = Stopwatch.StartNew();
            Assert.Throws<TimeoutException>(() => new HdcDeviceServices(session.Connection).ListDebugProcesses(250));
            Assert.True(elapsed.ElapsedMilliseconds < 1500);
        }
        finally
        {
            await stop.CancelAsync();
            if (producer is not null) await producer;
        }
    }

    private static byte[] Reply(HdcMessage request, HdcCommand command, byte[] payload)
        => HdcDaemonCodec.Encode(new(command, request.ChannelId, 0, payload));
}