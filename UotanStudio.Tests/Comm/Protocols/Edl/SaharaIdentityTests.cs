using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed partial class SaharaTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task HandshakeAsync_IdentityRequested_QueriesBeforeUploadingAndPreservesExactHash(int wordLength)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueCommandReady(transport);
        var word = Enumerable.Range(1, wordLength).Select(value => (byte)value).ToArray();
        var hash = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        EnqueueIdentityResponse(transport, 1, word);
        EnqueueIdentityResponse(transport, 2, word);
        EnqueueIdentityResponse(transport, 3, hash);
        EnqueueIdentityResponse(transport, 7, word);
        EnqueueHello(transport);
        EnqueueReadData(transport, 13, 0, 16);
        EnqueueEndOfImage(transport, 0);
        EnqueueDoneResponse(transport, 1);
        var protocol = new SaharaProtocol(transport) { QueryIdentityBeforeUpload = true };

        await protocol.HandshakeAsync(ImagesWithFirehose(), TestContext.Current.CancellationToken);

        var identity = Assert.IsType<SaharaDeviceIdentity>(protocol.DeviceIdentity);
        var expected = wordLength == 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(word)
            : BinaryPrimitives.ReadUInt64LittleEndian(word);
        Assert.Equal(expected, identity.SerialNumber);
        Assert.Equal(expected, identity.HardwareId);
        Assert.Equal(expected, identity.SblVersion);
        Assert.Equal(Convert.ToHexString(hash), identity.OemPublicKeyHashHex);
        Assert.Equal(SaharaMode.Command, MemoryMarshal.Read<SaharaHelloResponsePacket>(transport.Writes[0]).Mode);
        Assert.Equal(SaharaMode.ImageTxPending, MemoryMarshal.Read<SaharaHelloResponsePacket>(transport.Writes[10]).Mode);
        Assert.Equal(SaharaCommand.SwitchMode, ParseHeader(transport.Writes[9]).Command);
        Assert.Equal(new byte[16], transport.Writes[11]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 1)]
    [InlineData(9, 1)]
    [InlineData(4, 2)]
    [InlineData(uint.MaxValue, 1)]
    public async Task HandshakeAsync_InvalidIdentityResponse_StopsBeforeDataRequest(uint length, uint command)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueCommandReady(transport);
        EnqueueIdentityHeader(transport, command, length);
        var protocol = new SaharaProtocol(transport) { QueryIdentityBeforeUpload = true };

        await Assert.ThrowsAsync<SaharaException>(() => protocol.HandshakeAsync(
            ImagesWithFirehose(), TestContext.Current.CancellationToken));

        Assert.Null(protocol.DeviceIdentity);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(SaharaCommand.Execute, ParseHeader(transport.Writes[1]).Command);
    }

    [Fact]
    public async Task HandshakeAsync_OversizedHash_StopsBeforeHashDataRequest()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueCommandReady(transport);
        EnqueueIdentityResponse(transport, 1, new byte[4]);
        EnqueueIdentityResponse(transport, 2, new byte[4]);
        EnqueueIdentityHeader(transport, 3, 4097);
        var protocol = new SaharaProtocol(transport) { QueryIdentityBeforeUpload = true };

        await Assert.ThrowsAsync<SaharaException>(() => protocol.HandshakeAsync(
            ImagesWithFirehose(), TestContext.Current.CancellationToken));

        Assert.Null(protocol.DeviceIdentity);
        Assert.Equal(6, transport.Writes.Count);
    }

    [Fact]
    public async Task HandshakeAsync_IdentityReturnModeWrong_DoesNotPublishIdentityOrUpload()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueCommandReady(transport);
        EnqueueIdentityResponse(transport, 1, new byte[4]);
        EnqueueIdentityResponse(transport, 2, new byte[4]);
        EnqueueIdentityResponse(transport, 3, new byte[48]);
        EnqueueIdentityResponse(transport, 7, new byte[4]);
        var hello = BuildHelloPacket();
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(20), (uint)SaharaMode.Command);
        transport.Enqueue(hello);
        var protocol = new SaharaProtocol(transport) { QueryIdentityBeforeUpload = true };

        await Assert.ThrowsAsync<SaharaException>(() => protocol.HandshakeAsync(
            ImagesWithFirehose(), TestContext.Current.CancellationToken));

        Assert.Null(protocol.DeviceIdentity);
        Assert.Equal(10, transport.Writes.Count);
    }

    [Fact]
    public async Task InitializeAsync_ExplicitIdentityOptions_QueriesAndConfiguresSession()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueCommandReady(transport);
        EnqueueIdentityResponse(transport, 1, [1, 0, 0, 0]);
        EnqueueIdentityResponse(transport, 2, [2, 0, 0, 0]);
        EnqueueIdentityResponse(transport, 3, new byte[48]);
        EnqueueIdentityResponse(transport, 7, [3, 0, 0, 0]);
        EnqueueHello(transport);
        EnqueueEndOfImage(transport, 0);
        EnqueueDoneResponse(transport, 1);
        transport.Enqueue("<data><response value=\"ACK\"/></data>"u8.ToArray());
        using var session = new EdlSession(transport, new FirehoseConfiguration { AutoDetectSectorSize = false });
        using var image = new MemoryStream(new byte[16]);

        await session.InitializeAsync(image, new EdlInitializationOptions { QueryIdentityBeforeUpload = true },
            TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        Assert.Equal(1ul, session.Sahara!.DeviceIdentity!.SerialNumber);
    }

    private static void EnqueueIdentityResponse(ScriptedCommTransport transport, uint command, byte[] bytes)
    {
        EnqueueIdentityHeader(transport, command, (uint)bytes.Length);
        transport.Enqueue(bytes);
    }

    private static void EnqueueIdentityHeader(ScriptedCommTransport transport, uint command, uint length)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)SaharaCommand.ExecuteResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), command);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), length);
        transport.Enqueue(bytes);
    }
}