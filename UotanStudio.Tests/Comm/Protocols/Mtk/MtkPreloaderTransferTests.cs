using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task SendPreloaderPartitionDataAsync_AlignedPayload_EchoesCommandAndStreamsChecksum()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        transport.Enqueue([0x70]);
        transport.Enqueue([0x00, 0x00]);
        var before = transport.Writes.Count;

        await session.SendPreloaderPartitionDataAsync("preloader", payload, confirmWrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0x70 }, transport.Writes[before]);
        var name = transport.Writes[before + 1];
        Assert.Equal(64, name.Length);
        Assert.Equal("preloader"u8.ToArray(), name.Take(9));
        Assert.Equal(new byte[] { 0, 0, 0, 8 }, transport.Writes[before + 2]);
        Assert.Equal(payload, transport.Writes[before + 3]);
        Assert.Equal(new byte[] { 0x0C, 0x0A, 0x08, 0x06 }, transport.Writes[before + 4]);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task SendPreloaderPartitionDataAsync_LargePayload_StreamsAtDocumentedChunkWidth()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var payload = Enumerable.Range(0, 0x600).Select(value => (byte)(value & 0xFF)).ToArray();
        transport.Enqueue([0x70]);
        transport.Enqueue([0x00, 0x00]);
        var before = transport.Writes.Count;

        await session.SendPreloaderPartitionDataAsync("boot", payload, confirmWrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, transport.Writes.Skip(before + 3).Take(3).Count(bytes => bytes.Length == 0x200));
        Assert.Equal(payload, transport.Writes.Skip(before + 3).Take(3).SelectMany(bytes => bytes));
        Assert.Equal(4, transport.Writes[^1].Length);
    }

    [Fact]
    public async Task SendPreloaderPartitionDataAsync_NotConfirmed_SendsNothingAndKeepsBootloaderStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.SendPreloaderPartitionDataAsync(
            "boot", new byte[8], confirmWrite: false, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task SendPreloaderPartitionDataAsync_RequestStatusRejected_FaultsSession()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([0x70]);
        transport.Enqueue([0x0F, 0x00]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.SendPreloaderPartitionDataAsync(
            "boot", new byte[8], confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Equal(before + 3, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadPreloader32DirectAsync_KnownReply_PreservesBigEndianWireOrder()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var payload = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
        EnqueuePreloaderRead(transport, 0xD1, 0x200000, 2, payload);
        var words = await session.ReadPreloader32Async(0x200000, 2, TestContext.Current.CancellationToken);
        EnqueuePreloaderRead(transport, 0xD1, 0x200000, 2, payload);

        var direct = await session.ReadPreloader32DirectAsync(0x200000, 2, TestContext.Current.CancellationToken);

        Assert.Equal(new uint[] { 0x11223344, 0x55667788 }, words);
        Assert.Equal(payload, direct);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task ReadPreloader32DirectAsync_RefusedStatus_FaultsSession()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([0xD1]);
        transport.Enqueue(BigEndianWord(0x200000));
        transport.Enqueue(BigEndianWord(1));
        transport.Enqueue([0x0F, 0x00]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.ReadPreloader32DirectAsync(0x200000, 1, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadPreloaderA2Async_IotIdentityRegister_EchoesCommandAddressAndCount()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([0xA2]);
        transport.Enqueue(BigEndianWord(0x80000000));
        transport.Enqueue(BigEndianWord(1));
        transport.Enqueue([0x07, 0x66]);
        var before = transport.Writes.Count;

        var value = await session.ReadPreloaderA2Async(0x80000000, TestContext.Current.CancellationToken);

        Assert.Equal((ushort)0x0766, value);
        Assert.Equal(new byte[] { 0xA2 }, transport.Writes[before]);
        Assert.Equal(BigEndianWord(0x80000000), transport.Writes[before + 1]);
        Assert.Equal(BigEndianWord(1), transport.Writes[before + 2]);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task ReadPreloaderA2Async_CommandEchoMismatch_FaultsSession()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([0x00]);

        await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.ReadPreloaderA2Async(0x80000000, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    private static byte[] BigEndianWord(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}