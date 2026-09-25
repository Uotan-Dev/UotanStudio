using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public partial class MtkSessionTests
{
    [Fact]
    public async Task ReadXFlashPartitionToFileAsync_ValidSnapshot_RevalidatesThenPublishesExactPartition()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("boot.bin", [1, 2, 3]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        EnqueueGpt(transport, disk);
        var bytes = Enumerable.Range(0, 2048).Select(value => (byte)value).ToArray();
        EnqueueStorageRead(transport, bytes);
        var before = transport.Writes.Count;
        var progress = new List<long>();

        await session.ReadXFlashPartitionToFileAsync(snapshot, "boot", path,
            new InlineStorageProgress(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Equal(new long[] { 512, 1024, 1536, 2048 }, progress);
        Assert.Equal(new ulong[] { 512, 127 * 512, 1024, 95 * 512, 40 * 512 },
            transport.Writes.Skip(before).Where(value => value.Length == 56)
                .Select(value => BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadXFlashPartitionToFileAsync_MetadataChangeOrReadRejection_PreservesDestination(bool rejectRead)
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("boot.bin", [1, 2, 3]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        if (!rejectRead)
        {
            disk[1024 + 48] = 1;
            GptReaderTests.FixArrays(disk);
        }
        EnqueueGpt(transport, disk);
        if (rejectRead)
        {
            EnqueueStorageRead(transport, new byte[2048], 0xC0070004);
        }
        var before = transport.Writes.Count;

        if (rejectRead)
        {
            await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashPartitionToFileAsync(
                snapshot, "boot", path, cancellationToken: TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => session.ReadXFlashPartitionToFileAsync(
                snapshot, "boot", path, cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Equal(rejectRead ? 5 : 4, transport.Writes.Skip(before).Count(value => value.Length == 56));
        Assert.Equal(rejectRead ? MtkSessionState.Faulted : MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashPartitionToFileAsync_ForeignSnapshotOrWrongCase_DoesNotReadOrCreateFile()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "boot.bin");
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        using var foreignTransport = new ScriptedCommTransport();
        using var foreign = await CreateGptSessionAsync(foreignTransport);
        EnqueueGpt(foreignTransport, GptReaderTests.CreateDisk(512));
        var snapshot = await foreign.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXFlashPartitionToFileAsync(
            snapshot, "boot", path, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => session.ReadXFlashPartitionToFileAsync(
            snapshot, "BOOT", path, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task ReadXFlashPartitionToFileAsync_QueuedRead_CannotInterleaveAfterGptRevalidation()
    {
        using var dir = new TempDirectory();
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        EnqueueGpt(transport, disk);
        EnqueueStorageRead(transport, new byte[2048]);
        EnqueueStorageRead(transport, new byte[512]);
        using var output = new MemoryStream();
        Task? queued = null;
        var before = transport.Writes.Count;
        transport.OnWrite = bytes =>
        {
            if (bytes.Length == 56 && queued is null)
            {
                queued = session.ReadXFlashStorageAsync(8, 0, 512, output,
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
            }
            return [];
        };

        await session.ReadXFlashPartitionToFileAsync(snapshot, "boot", Path.Combine(dir.Path, "boot.bin"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        await queued;

        Assert.Equal(new ulong[] { 512, 127 * 512, 1024, 95 * 512, 40 * 512, 0 },
            transport.Writes.Skip(before).Where(value => value.Length == 56)
                .Select(value => BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8))));
        Assert.Equal(512, output.Length);
    }

    [Fact]
    public async Task ReadXFlashPartitionToFileAsync_CancelledDuringData_KeepsExistingDestination()
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("boot.bin", [1, 2, 3]);
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var disk = GptReaderTests.CreateDisk(512);
        EnqueueGpt(transport, disk);
        var snapshot = await session.ReadXFlashGptAsync(8, TestContext.Current.CancellationToken);
        EnqueueGpt(transport, disk);
        EnqueueStorageRead(transport, new byte[2048]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXFlashPartitionToFileAsync(
            snapshot, "boot", path, new InlineStorageProgress(_ => cancellation.Cancel()), cancellation.Token));

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }
}