using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public partial class FirehoseGptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadPartitionToFileAsync_ValidObservation_PublishesExactBytes(bool existing)
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "boot.bin");
        if (existing)
        {
            dir.WriteBytes("boot.bin", [1, 2, 3]);
        }
        var disk = GptReaderTests.CreateDisk(512);
        var expected = Enumerable.Range(0, 2048).Select(value => (byte)value).ToArray();
        expected.CopyTo(disk, 40 * 512);
        using var transport = Transport(disk, 512);
        using var protocol = Protocol(transport, 512);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
        var progress = new List<long>();

        await protocol.ReadPartitionToFileAsync(snapshot, "boot", path, new InlineBackupProgress(progress.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Equal(2048, progress[^1]);
        Assert.Equal(new[] { "getstorageinfo", "read", "read", "read", "read", "read" },
            Commands(transport).Skip(before).Select(command => command.Name.LocalName));
        var read = Commands(transport)[^1];
        Assert.Equal(40, (int)read.Attribute("start_sector")!);
        Assert.Equal(4, (int)read.Attribute("num_partition_sectors")!);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("nak")]
    [InlineData("cancel")]
    public async Task ReadPartitionToFileAsync_FailedRead_PreservesExistingOutput(string failure)
    {
        using var dir = new TempDirectory();
        var path = dir.WriteBytes("boot.bin", [1, 2, 3]);
        var disk = GptReaderTests.CreateDisk(512);
        using var transport = Transport(disk, 512);
        using var protocol = Protocol(transport, 512);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (failure == "metadata")
        {
            disk[1024 + 48] = 1;
            GptReaderTests.FixArrays(disk);
        }
        if (failure == "nak")
        {
            var respond = transport.OnWrite!;
            transport.OnWrite = bytes =>
            {
                var result = respond(bytes).ToArray();
                if (Encoding.UTF8.GetString(bytes).Contains("partition.bin", StringComparison.Ordinal))
                {
                    result[^1] = Encoding.UTF8.GetBytes("<data><response value='NAK' rawmode='false'/></data>");
                }
                return result;
            };
        }
        var task = protocol.ReadPartitionToFileAsync(snapshot, "boot", path,
            failure == "cancel" ? new InlineBackupProgress(_ => cancellation.Cancel()) : null, cancellation.Token);
        switch (failure)
        {
            case "metadata": await Assert.ThrowsAsync<InvalidDataException>(() => task); break;
            case "nak": await Assert.ThrowsAnyAsync<EdlException>(() => task); break;
            default: await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task); break;
        }

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.Equal(failure != "metadata", protocol.RequiresReconnect);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("slot")]
    [InlineData("name")]
    public async Task ReadPartitionToFileAsync_InvalidBinding_DoesNotReadOrCreateFiles(string failure)
    {
        using var dir = new TempDirectory();
        var disk = GptReaderTests.CreateDisk(512);
        using var transport = Transport(disk, 512);
        using var protocol = Protocol(transport, 512);
        using var otherTransport = Transport(disk, 512);
        using var other = Protocol(otherTransport, 512);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);
        if (failure == "slot")
        {
            protocol.Configuration.Slot = 1;
        }
        var target = failure == "owner" ? other : protocol;
        var before = transport.Writes.Count;
        var task = target.ReadPartitionToFileAsync(snapshot, failure == "name" ? "BOOT" : "boot",
            Path.Combine(dir.Path, "boot.bin"), cancellationToken: TestContext.Current.CancellationToken);

        if (failure == "name")
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => task);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        }
        Assert.Empty(Directory.GetFiles(dir.Path));
        Assert.Equal(before, transport.Writes.Count);
        Assert.Empty(otherTransport.Writes);
    }

    [Fact]
    public async Task ReadPartitionToFileAsync_QueuedProbe_WaitsThroughPartitionRead()
    {
        using var dir = new TempDirectory();
        using var transport = Transport(GptReaderTests.CreateDisk(512), 512);
        using var protocol = Protocol(transport, 512);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);
        var before = transport.Writes.Count;
        var respond = transport.OnWrite!;
        Task<FirehoseResponse>? queued = null;
        transport.OnWrite = bytes =>
        {
            if (queued is null)
            {
                queued = protocol.NopAsync(TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
            }
            return respond(bytes);
        };

        await protocol.ReadPartitionToFileAsync(snapshot, "boot", Path.Combine(dir.Path, "boot.bin"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(queued);
        Assert.True((await queued).IsAck);

        Assert.Equal(new[] { "getstorageinfo", "read", "read", "read", "read", "read", "nop" },
            Commands(transport).Skip(before).Select(command => command.Name.LocalName));
    }

    [Fact]
    public async Task ReadPartitionToFileAsync_PublicationFailure_CleansStagingWithoutFaultingProtocol()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "occupied");
        Directory.CreateDirectory(path);
        using var transport = Transport(GptReaderTests.CreateDisk(512), 512);
        using var protocol = Protocol(transport, 512);
        var snapshot = await protocol.ReadGptAsync(0, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => protocol.ReadPartitionToFileAsync(snapshot, "boot", path,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(dir.Path));
        Assert.False(protocol.RequiresReconnect);
    }

    private sealed class InlineBackupProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}