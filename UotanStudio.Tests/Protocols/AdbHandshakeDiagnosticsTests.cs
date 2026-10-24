using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class AdbHandshakeDiagnosticsTests
{
    [Fact]
    public void ParseHeader_CorruptMagic_ReportsDecodedHeaderWords()
    {
        var exception = Assert.Throws<InvalidDataException>(() => AdbMessaging.ParseHeader(CorruptHeader()));

        Assert.Contains("Invalid ADB message magic!", exception.Message, StringComparison.Ordinal);
        Assert.Contains("command=0x00424441", exception.Message, StringComparison.Ordinal);
        Assert.Contains("magic=0x0000005A", exception.Message, StringComparison.Ordinal);
        Assert.Contains("expected=0xFFBDBBBE", exception.Message, StringComparison.Ordinal);
        Assert.Contains("first8=4144420000000000", exception.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 20000)]
    public void Handshake_CorruptFirstFrameThenCnxn_ResynchronizesOnceAndConnects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.False(cancellationToken.IsCancellationRequested);
        var transport = new ScriptedCommTransport();
        transport.Enqueue(CorruptHeader());
        transport.Enqueue(CnxnFrame());
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);

        session.Connect();

        Assert.True(session.IsConnected);
        Assert.Equal(1, transport.ResetCount);
    }

    [Fact(Timeout = 20000)]
    public void Handshake_CorruptFirstFrameTwice_ResynchronizesOnlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.False(cancellationToken.IsCancellationRequested);
        var transport = new ScriptedCommTransport();
        transport.Enqueue(CorruptHeader());
        transport.Enqueue(CorruptHeader());
        using var authentication = AdbAuthentication.CreateNew();
        using var session = new AdbSession(transport, authentication);

        Assert.ThrowsAny<Exception>(session.Connect);

        Assert.Equal(1, transport.ResetCount);
    }

    private static byte[] CorruptHeader()
    {
        var header = new byte[AdbConstants.HeaderSize];
        header[0] = 0x41;
        header[1] = 0x44;
        header[2] = 0x42;
        header[3] = 0x00;
        header[20] = 0x5A;
        return header;
    }

    private static byte[] CnxnFrame() =>
        AdbMessaging.Serialize(new AdbMessage(AdbCommand.Cnxn, AdbConstants.ProtocolVersion,
            AdbConstants.MaxPayload, "device::ro.product.name=tests;features=cmd,shell_v2"u8.ToArray()));
}