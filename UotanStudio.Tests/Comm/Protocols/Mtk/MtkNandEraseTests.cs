using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkXFlashNandEraseMode.Normal)]
    [InlineData(MtkXFlashNandEraseMode.Force)]
    [InlineData(MtkXFlashNandEraseMode.MarkBadBlock)]
    public async Task EraseXFlashNandStorageAsync_ExplicitAction_SendsEraseUnionAndWaitsForCompletion(MtkXFlashNandEraseMode mode)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport);
        var policy = NandOptions().Policy with
        {
            AddressType = MtkXFlashNandAddressType.Physical,
            StorageType = MtkXFlashNandStorageType.Tlc,
            Region = 3
        };
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0x40040004);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0x40040005);
        var before = transport.Writes.Count;

        await session.EraseXFlashNandStorageAsync(786432, 131072, policy, mode,
            cancellationToken: TestContext.Current.CancellationToken);

        var frames = NandWrittenFrames(transport, before);
        Assert.Equal(3, frames.Count);
        Assert.Equal(0x010003u, BinaryPrimitives.ReadUInt32LittleEndian(frames[0]));
        var parameters = frames[1];
        Assert.Equal(0x13u, BinaryPrimitives.ReadUInt32LittleEndian(parameters));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan(4)));
        Assert.Equal(786432ul, BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(8)));
        Assert.Equal(131072ul, BinaryPrimitives.ReadUInt64LittleEndian(parameters.AsSpan(16)));
        Assert.Equal(new uint[] { 0, 1, 0, 3, (uint)mode, 0, 0, 0 },
            Enumerable.Range(0, 8).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(parameters.AsSpan(24 + index * 4))));
        Assert.Equal(new byte[4], frames[2]);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(2048L, 131072L, 0, 1000)]
    [InlineData(0L, 2048L, 0, 1000)]
    [InlineData(786432L, 131072L, 0, 1000)]
    [InlineData(0L, 131072L, 9, 1000)]
    [InlineData(0L, 131072L, 0, 0)]
    [InlineData(0L, 131072L, 0, 86400001)]
    public async Task EraseXFlashNandStorageAsync_InvalidActionOrRange_RejectsBeforeIo(
        long offset, long length, int mode, int timeout)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EraseXFlashNandStorageAsync(
            offset, length, NandOptions().Policy, (MtkXFlashNandEraseMode)mode, timeout, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("unfinished")]
    [InlineData("timeout")]
    [InlineData("unbounded-wait")]
    public async Task EraseXFlashNandStorageAsync_UnconfirmedAction_FaultsWithoutRetry(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateNandSessionAsync(transport);
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, failure switch
        {
            "rejected" => 0xC0070004,
            "unfinished" => 0,
            _ => 0x40040004
        });
        if (failure is "timeout" or "unbounded-wait")
        {
            EnqueueXFlashValue(transport, failure == "timeout" ? 60000u : 60001u);
        }
        var before = transport.Writes.Count;

        if (failure == "timeout")
        {
            await Assert.ThrowsAsync<TimeoutException>(() => session.EraseXFlashNandStorageAsync(
                0, 131072, NandOptions().Policy, MtkXFlashNandEraseMode.Normal, 100, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseXFlashNandStorageAsync(
                0, 131072, NandOptions().Policy, MtkXFlashNandEraseMode.Normal,
                cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Equal(before + 4, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EraseXFlashNandStorageAsync(
            0, 131072, NandOptions().Policy, MtkXFlashNandEraseMode.Normal,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before + 4, transport.Writes.Count);
    }

    [Fact]
    public async Task NandStorageAsync_EmmcSession_RejectsAllNandCommandsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStorageSessionAsync(transport);
        using var stream = new MemoryStream(new byte[2048]);
        var options = NandOptions();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXFlashNandStorageAsync(
            0, 2048, options, stream, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteXFlashNandStorageAsync(
            0, 2048, options, stream, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EraseXFlashNandStorageAsync(
            0, 131072, options.Policy, MtkXFlashNandEraseMode.Normal,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }
}