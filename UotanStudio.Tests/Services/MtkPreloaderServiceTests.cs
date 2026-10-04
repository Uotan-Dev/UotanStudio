using System.Buffers.Binary;
using System.Security.Cryptography;
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
    public async Task DumpInternalFlashAsync_CompleteWindow_PublishesByteExactFileAndRemovesStaging()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var expected = FlashPattern(16);
        EnqueuePreloaderRead(transport, PreloaderRead32, 0x0000_0000, 4, expected);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "iflash.bin");
        var reports = new List<FlashProgress>();

        var record = await MtkPreloaderService.DumpInternalFlashAsync(session, 0x0000_0000, 16, 16,
            confirmFlashMapping: false, destination, new ListProgress(reports),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.Equal(16, record.ByteLength);
        Assert.Equal(16, record.RequestedBytes);
        Assert.Equal(16, record.StepBytes);
        Assert.Equal(0x0000_0000u, record.StartOffset);
        Assert.Equal(destination, record.DestinationPath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), record.Sha256Hex);
        Assert.Equal(100, reports[^1].Percent);
        Assert.Single(Directory.GetFiles(directory.Path));
        Assert.True(transport.Writes.Count > before);
    }

    [Fact]
    public async Task DumpInternalFlashAsync_ShortReadFault_KeepsExistingFileAndRemovesStaging()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var first = FlashPattern(16);
        EnqueuePreloaderRead(transport, PreloaderRead32, 0x0000_0000, 4, first);
        transport.Enqueue([PreloaderRead32]);
        transport.Enqueue(BigEndian(0x10));
        transport.Enqueue(BigEndian(4));
        transport.Enqueue([0x00, 0x00]);
        transport.Enqueue(new byte[8]);
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "iflash.bin");
        var existing = "PRE-EXISTING IMAGE"u8.ToArray();
        File.WriteAllBytes(destination, existing);

        await Assert.ThrowsAsync<MtkProtocolException>(() => MtkPreloaderService.DumpInternalFlashAsync(
            session, 0x0000_0000, 32, 16, confirmFlashMapping: false, destination, null,
            TestContext.Current.CancellationToken));

        Assert.Equal(existing, File.ReadAllBytes(destination));
        Assert.Single(Directory.GetFileSystemEntries(directory.Path));
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task DumpInternalFlashAsync_PreCancelledToken_WritesNothingAndKeepsDirectoryClean()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;
        using var directory = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkPreloaderService.DumpInternalFlashAsync(
            session, 0x0000_0000, 16, 16, confirmFlashMapping: false,
            Path.Combine(directory.Path, "iflash.bin"), null, cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

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
    public async Task WriteWindowAsync_NotConfirmed_SendsZeroDeviceBytesAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MtkPreloaderService.WriteWindowAsync(
            session, 0x0010_0000, new byte[] { 1, 2, 3, 4 }, confirmed: false,
            TestContext.Current.CancellationToken));

        Assert.Contains("requires an explicit confirmation", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task WriteWindowAsync_UnalignedAddress_RejectsBeforeAnyIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        var error = await Assert.ThrowsAsync<ArgumentException>(() => MtkPreloaderService.WriteWindowAsync(
            session, 0x0010_0001, new byte[] { 1, 2, 3, 4 }, confirmed: true,
            TestContext.Current.CancellationToken));

        Assert.Contains("not 32-bit aligned", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
    }

    [Fact]
    public async Task WriteWindowAsync_WindowLeavingAddressSpace_RejectsBeforeAnyIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MtkPreloaderService.WriteWindowAsync(
            session, 0xFFFF_FFF0, new byte[0x20], confirmed: true, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
    }

    [Fact]
    public async Task WriteWindowAsync_WithinCeiling_IssuesSingleWriteAndReportsSubmittedBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        EnqueuePreloaderWrite32(transport, 0x0010_0000, [0x0403_0201]);
        EnqueuePreloaderWrite32(transport, 0x0010_0004, [0x0807_0605]);

        var submitted = await MtkPreloaderService.WriteWindowAsync(session, 0x0010_0000,
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, confirmed: true, TestContext.Current.CancellationToken);

        Assert.Equal(8, submitted);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task WriteWindowAsync_OverCeilingPayload_CancelledBeforeFirstChunkSendsZeroBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreatePreloaderSessionAsync(transport);
        var before = transport.Writes.Count;
        var reads = transport.ReadCount;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkPreloaderService.WriteWindowAsync(
            session, 0x0010_0000, new byte[MtkDaMemoryAccess.MaximumWordLoopWriteBytes + 4],
            confirmed: true, cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(reads, transport.ReadCount);
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

    private static byte[] FlashPattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)(index * 5 + 1);
        }

        return bytes;
    }

    private sealed class ListProgress(List<FlashProgress> reports) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => reports.Add(value);
    }
}