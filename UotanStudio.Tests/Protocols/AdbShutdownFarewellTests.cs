using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class AdbShutdownFarewellTests
{
    [Fact(Timeout = 15000)]
    public async Task SendShutdownAsync_OpenStream_WritesCloseFrameForIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new ScriptedCommTransport();
        transport.Enqueue(CnxnFrame());
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        var stream = (AdbStream)session.OpenStream("shell:true");
        stream.SetRemoteId(7);

        await session.SendShutdownAsync(TimeSpan.FromSeconds(1), cancellationToken);

        Assert.Equal((stream.LocalId, (uint)7), CloseFrame(transport));
    }

    [Fact(Timeout = 15000)]
    public async Task SendShutdownAsync_NoOpenStreams_WritesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new ScriptedCommTransport();
        transport.Enqueue(CnxnFrame());
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        var writesBefore = transport.Writes.Count;

        await session.SendShutdownAsync(TimeSpan.FromSeconds(1), cancellationToken);

        Assert.Equal(writesBefore, transport.Writes.Count);
    }

    private static (uint LocalId, uint RemoteId) CloseFrame(ScriptedCommTransport transport)
    {
        foreach (var write in transport.Writes)
        {
            if (write.Length == AdbConstants.HeaderSize)
            {
                var (command, arg0, arg1, _, _) = AdbMessaging.ParseHeader(write);
                if (command == AdbCommand.Clse)
                {
                    return (arg0, arg1);
                }
            }
        }

        return (uint.MaxValue, uint.MaxValue);
    }

    private static byte[] CnxnFrame() =>
        AdbMessaging.Serialize(new AdbMessage(AdbCommand.Cnxn, AdbConstants.ProtocolVersion,
            AdbConstants.MaxPayload, "device::ro.product.name=tests;features=cmd,shell_v2"u8.ToArray()));
}