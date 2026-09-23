using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class FlashBundleSourceTests
{
    [Theory]
    [InlineData(FlashBundlePhase.Erasing)]
    [InlineData(FlashBundlePhase.Programming)]
    [InlineData(FlashBundlePhase.Patching)]
    [InlineData(FlashBundlePhase.Wiping)]
    [InlineData(FlashBundlePhase.UfsProvisioning)]
    public async Task FlashBundleAsync_QueuedPublicProbe_CannotInterleaveAnyBundlePhase(FlashBundlePhase phase)
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("source.img", new byte[512]);
        var manifest = directory.Write("rawprogram.xml", """
            <data>
              <program filename="" start_sector="0" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/>
              <program filename="source.img" start_sector="1" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/>
            </data>
            """);
        var patch = directory.Write("patch.xml", """
            <data><patch filename="DISK" start_sector="1" byte_offset="0" size_in_bytes="4" value="0" SECTOR_SIZE_IN_BYTES="512"/></data>
            """);
        var ufs = directory.Write("ufs-provision.xml", """
            <data>
              <ufs type="common" BNumberLU="1"/>
              <ufs type="body" LUNum="0"/>
              <ufs type="epilogue" commit="0"/>
            </data>
            """);
        using var transport = Transport();
        using var session = Session(transport);
        Task<FirehoseResponse>? queued = null;
        var progress = new InlineProgress(value =>
        {
            if (value.Phase == phase && queued is null)
            {
                var before = transport.Writes.Count;
                queued = session.Firehose!.NopAsync(TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
                Assert.Equal(before, transport.Writes.Count);
            }
        });

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = manifest,
            PatchPath = patch,
            ImageDirectory = directory.Path,
            UfsProvisionPath = ufs,
            WipeFirstSectors = 1
        }, progress, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.NotNull(queued);
        Assert.True((await queued.WaitAsync(TestContext.Current.CancellationToken)).IsAck);
        var commands = BundleCommands(transport);
        Assert.Equal(new[] { "configure", "getstorageinfo", "erase", "program", "patch", "program", "ufs", "ufs", "ufs", "nop" }, commands);
    }

    [Fact]
    public async Task FlashBundleAsync_InitializationQueuesProbe_HoldsLeaseThroughLastProgram()
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("source.img", new byte[512]);
        using var transport = Transport();
        using var session = Session(transport);
        var respond = transport.OnWrite!;
        Task<FirehoseResponse>? queued = null;
        transport.OnWrite = bytes =>
        {
            if (Encoding.UTF8.GetString(bytes).Contains("<configure ", StringComparison.Ordinal))
            {
                queued = session.Firehose!.NopAsync(TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
            }
            return respond(bytes);
        };

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            Manifest(directory), null, directory.Path, null, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.NotNull(queued);
        await queued.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "configure", "getstorageinfo", "program", "nop" }, BundleCommands(transport));
    }

    [Fact]
    public async Task FlashBundleAsync_QueuedBundleCancelled_DoesNotInterruptOwnerOrLeakSources()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteBytes("source.img", new byte[512]);
        var manifest = Manifest(directory);
        using var transport = Transport();
        using var session = Session(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<FlashBundleResult>? queued = null;
        var progress = new InlineProgress(value =>
        {
            if (value.Phase == FlashBundlePhase.Programming)
            {
                var before = transport.Writes.Count;
                queued = new FlashBundleOrchestrator(session).FlashBundleAsync(
                    manifest, null, directory.Path, null, cancellation.Token);
                Assert.False(queued.IsCompleted);
                cancellation.Cancel();
                Assert.Equal(before, transport.Writes.Count);
            }
        });

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            manifest, null, directory.Path, progress, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.NotNull(queued);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(new[] { "configure", "getstorageinfo", "program" }, BundleCommands(transport));
        Assert.False(session.Firehose!.RequiresReconnect);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task FlashBundleAsync_DryRun_DoesNotWaitForDeviceLease()
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("source.img", new byte[512]);
        using var transport = Transport();
        using var session = Session(transport);
        using var held = await session.Firehose!.EnterOperationAsync(TestContext.Current.CancellationToken);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = Manifest(directory),
            ImageDirectory = directory.Path,
            DryRun = true
        }, null, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task FlashBundleAsync_GeometryMismatch_ReleasesLeaseForSynchronizedProbe()
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("source.img", new byte[512]);
        using var transport = Transport();
        using var session = Session(transport);
        var respond = transport.OnWrite!;
        Task<FirehoseResponse>? queued = null;
        transport.OnWrite = bytes =>
        {
            if (Encoding.UTF8.GetString(bytes).Contains("<getstorageinfo ", StringComparison.Ordinal))
            {
                queued = session.Firehose!.NopAsync(TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
                return [Encoding.UTF8.GetBytes("""<data><log value='{"storage_info":{"block_size":4096,"total_blocks":100}}'/><response value='ACK'/></data>""")];
            }
            return respond(bytes);
        };

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            Manifest(directory), null, directory.Path, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(queued);
        Assert.True((await queued.WaitAsync(TestContext.Current.CancellationToken)).IsAck);
        Assert.Equal(new[] { "configure", "getstorageinfo", "nop" }, BundleCommands(transport));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlashBundleAsync_InFlightFailure_ReleasesLeaseButRejectsQueuedCommands(bool cancel)
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("source.img", new byte[512]);
        using var transport = Transport();
        using var session = Session(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var respond = transport.OnWrite!;
        Task<FirehoseResponse>? queued = null;
        var progress = new InlineProgress(value =>
        {
            if (value.Phase == FlashBundlePhase.Programming)
            {
                queued = session.Firehose!.NopAsync(TestContext.Current.CancellationToken);
                Assert.False(queued.IsCompleted);
            }
        });
        transport.OnWrite = bytes =>
        {
            if (bytes[0] != '<')
            {
                if (cancel)
                {
                    cancellation.Cancel();
                }
                return [Encoding.UTF8.GetBytes("<data><response value='NAK' rawmode='false'/></data>")];
            }
            return respond(bytes);
        };

        var operation = new FlashBundleOrchestrator(session).FlashBundleAsync(
            Manifest(directory), null, directory.Path, progress, cancellation.Token);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            Assert.False((await operation).IsSuccess);
        }
        Assert.NotNull(queued);
        await Assert.ThrowsAsync<EdlException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(session.Firehose!.RequiresReconnect);
        Assert.Equal(new[] { "configure", "getstorageinfo", "program" }, BundleCommands(transport));
    }

    private static string[] BundleCommands(ScriptedCommTransport transport) => transport.Writes
        .Where(bytes => bytes[0] == '<')
        .Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single().Name.LocalName).ToArray();
}