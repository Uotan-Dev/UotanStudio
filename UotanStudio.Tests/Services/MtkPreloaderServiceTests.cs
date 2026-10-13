using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MtkPreloaderServiceTests
{
    private const byte PreloaderRead32 = 0xD1;
    private const byte PreloaderWrite32 = 0xD4;

    [Fact]
    public async Task PeekWindowAsync_KnownWindow_ReturnsReferenceLittleEndianBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        EnqueuePreloaderRead(transport, PreloaderRead32, 0x0000_1000, 1,
            [0x11, 0x22, 0x33, 0x44]);

        var data = await MtkPreloaderService.PeekWindowAsync(session, 0x0000_1000, 4,
            TestContext.Current.CancellationToken);

        Assert.Equal([0x44, 0x33, 0x22, 0x11], data);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task ReturnToBootRomAsync_NotConfirmed_SendsZeroDeviceBytesAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MtkPreloaderService.ReturnToBootRomAsync(
            session, timeoutSeconds: 0, confirmed: false, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task ReturnToBootRomAsync_Confirmed_DrivesResetSequenceThroughSession()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        EnqueuePreloaderWrite32(transport, 0x1001_A100, [0xAD98]);
        EnqueuePreloaderWrite32(transport, 0x1001_A108, [1]);
        EnqueuePreloaderWrite32(transport, 0x1001_A100, [0]);
        EnqueuePreloaderWrite32(transport, 0x1001_A0E0, [0x444C_FFFD]);

        await MtkPreloaderService.ReturnToBootRomAsync(session, timeoutSeconds: 0, confirmed: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task DisableWatchdogAsync_NotConfirmed_SendsZeroDeviceBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MtkPreloaderService.DisableWatchdogAsync(
            session, confirmed: false, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
    }

    [Fact]
    public async Task DisableWatchdogAsync_ConfirmedKnownChip_DrivesWriteThroughSession()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        EnqueuePreloaderWrite32(transport, 0x1000_7000, [0x2200_0064]);

        await MtkPreloaderService.DisableWatchdogAsync(session, confirmed: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task JumpToPartitionAsync_NotConfirmed_SendsZeroDeviceBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MtkPreloaderService.JumpToPartitionAsync(
            session, "boot", confirmed: false, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
    }

    [Fact]
    public async Task JumpToPartitionAsync_ConfirmedKnownName_DrivesJumpThroughSession()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([0x71]);
        transport.Enqueue([0x00, 0x00]);

        await MtkPreloaderService.JumpToPartitionAsync(session, "boot", confirmed: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task JumpToBootLoaderAsync_NotConfirmed_SendsZeroDeviceBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MtkPreloaderService.JumpToBootLoaderAsync(
            session, confirmed: false, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task JumpToBootLoaderAsync_Confirmed_DrivesJumpThroughSession()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        transport.Enqueue([0xD6]);
        transport.Enqueue([0x00, 0x00]);
        transport.Enqueue([0x00, 0x00]);

        await MtkPreloaderService.JumpToBootLoaderAsync(session, confirmed: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    private static async Task<MtkSession> CreatePreloaderSessionAsync(ScriptedCommTransport transport)
    {
        transport.Enqueue(
        [
            0x5F, 0xF5, 0xAF, 0xFA,
            0xFD, 0x07, 0x66, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
            0xD8, 0, 0, 0, 0x17, 0, 0
        ]);
        var session = new MtkSession(transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        return session;
    }

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static void EnqueuePreloaderRead(ScriptedCommTransport transport, byte command, uint address,
        int words, byte[] payload)
    {
        transport.Enqueue([command]);
        transport.Enqueue(BigEndian(address));
        transport.Enqueue(BigEndian((uint)words));
        transport.Enqueue([0x00, 0x00]);
        transport.Enqueue(payload);
        transport.Enqueue([0x00, 0x00]);
    }

    private static void EnqueuePreloaderWrite32(ScriptedCommTransport transport, uint address, uint[] values)
    {
        transport.Enqueue([PreloaderWrite32]);
        transport.Enqueue(BigEndian(address));
        transport.Enqueue(BigEndian((uint)values.Length));
        transport.Enqueue([0x00, 0x00]);
        foreach (var value in values)
        {
            transport.Enqueue(BigEndian(value));
        }

        transport.Enqueue([0x00, 0x00]);
    }

    private sealed class ListProgress(List<FlashProgress> reports) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => reports.Add(value);
    }
}