using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class AdbConnectionFailureTests
{
    [Fact(Timeout = 20000)]
    public void OpenStream_TransportWriteFailsAfterHandshake_MarksSessionDisconnected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.False(cancellationToken.IsCancellationRequested);
        var transport = new ScriptedCommTransport();
        transport.Enqueue(CnxnFrame());
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        Assert.True(session.IsConnected);

        transport.RejectWrite = static (_, _, _) => true;

        Assert.Throws<IOException>(() => session.OpenStream("shell:ps -A -o PID,NAME"));

        Assert.False(session.IsConnected);
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact(Timeout = 20000)]
    public void OpenStream_AfterTransportWriteFailed_DoesNotWriteIntoTheFailedTransportAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.False(cancellationToken.IsCancellationRequested);
        var transport = new ScriptedCommTransport();
        transport.Enqueue(CnxnFrame());
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);
        session.Connect();
        transport.RejectWrite = static (_, _, _) => true;
        Assert.Throws<IOException>(() => session.OpenStream("shell:ps -A -o PID,NAME"));
        var writesWhenFailed = transport.Writes.Count;

        Assert.Throws<InvalidOperationException>(() => session.OpenStream("shell:getprop ro.product.model"));

        Assert.Equal(writesWhenFailed, transport.Writes.Count);
    }

    private static byte[] CnxnFrame() =>
        AdbMessaging.Serialize(new AdbMessage(AdbCommand.Cnxn, AdbConstants.ProtocolVersion,
            AdbConstants.MaxPayload, "device::ro.product.name=tests;features=cmd,shell_v2"u8.ToArray()));
}