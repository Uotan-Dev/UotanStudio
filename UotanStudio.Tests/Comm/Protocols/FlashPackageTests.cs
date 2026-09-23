using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class FlashBundleSourceTests
{
    [Fact]
    public async Task FlashPackageAsync_ManifestsAndCallerOptionsChange_UsesCapturedInputsAndOneLease()
    {
        using var directory = new TempDirectory();
        var source = directory.WriteBytes("source.img", Enumerable.Repeat((byte)0x42, 512).ToArray());
        var first = PackageOption(directory, "first.xml", 1);
        var second = PackageOption(directory, "second.xml", 2);
        using var transport = Transport();
        using var session = Session(transport);
        Task<FirehoseResponse>? queued = null;
        var completed = 0;
        var progress = new InlineProgress(value =>
        {
            if (value.Phase != FlashBundlePhase.Completed)
            {
                return;
            }
            completed++;
            if (completed != 1)
            {
                Assert.False(queued!.IsCompleted);
                return;
            }
            queued = session.Firehose!.NopAsync(TestContext.Current.CancellationToken);
            Assert.False(queued.IsCompleted);
            File.WriteAllText(second.RawProgramPath, "invalid XML");
            second.RawProgramPath = "missing";
            second.SkipProgram = true;
            second.Slot = 9;
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<IOException>(() => File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            }
            else
            {
                File.Move(source, Path.Combine(directory.Path, "original.img"));
                File.WriteAllBytes(source, new byte[512]);
            }
        });

        var results = await new FlashBundleOrchestrator(session).FlashPackageAsync(
            [first, second], progress, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.IsSuccess, string.Join("; ", result.Errors)));
        Assert.Equal(2, completed);
        Assert.NotNull(queued);
        Assert.True((await queued.WaitAsync(TestContext.Current.CancellationToken)).IsAck);
        Assert.Equal(new[] { "configure", "getstorageinfo", "getstorageinfo", "getstorageinfo", "program",
            "getstorageinfo", "program", "nop" }, BundleCommands(transport));
        var payloads = transport.Writes.Where(bytes => bytes.Length == 512).ToArray();
        Assert.Equal(2, payloads.Length);
        Assert.All(payloads, bytes => Assert.All(bytes, value => Assert.Equal((byte)0x42, value)));
        Assert.Contains("start_sector=\"2\"", Encoding.UTF8.GetString(transport.Writes[^3]), StringComparison.Ordinal);
        using var exclusive = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task FlashPackageAsync_LaterPreparationFails_ReleasesEarlierSourcesWithoutDeviceIo()
    {
        using var directory = new TempDirectory();
        var source = directory.WriteBytes("source.img", new byte[512]);
        var first = PackageOption(directory, "first.xml", 1);
        var second = PackageOption(directory, "second.xml", 2);
        File.WriteAllText(second.RawProgramPath, "<data><program filename='missing.img'/></data>");
        using var transport = Transport();
        using var session = Session(transport);

        await Assert.ThrowsAsync<FileNotFoundException>(() => new FlashBundleOrchestrator(session)
            .FlashPackageAsync([first, second], null, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        using var exclusive = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task FlashPackageAsync_CancelledWhileWaiting_ReleasesAllRetainedSources()
    {
        using var directory = new TempDirectory();
        var source = directory.WriteBytes("source.img", new byte[512]);
        using var transport = Transport();
        using var session = Session(transport);
        using var lease = await session.Firehose!.EnterOperationAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = new FlashBundleOrchestrator(session).FlashPackageAsync(
            [PackageOption(directory, "first.xml", 1), PackageOption(directory, "second.xml", 2)], null, cancellation.Token);
        Assert.False(operation.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.Empty(transport.Writes);
        using var exclusive = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task FlashPackageAsync_DryRun_DoesNotWaitForLeaseOrWrite()
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("source.img", new byte[512]);
        var first = PackageOption(directory, "first.xml", 1);
        var second = PackageOption(directory, "second.xml", 2);
        first.DryRun = second.DryRun = true;
        using var transport = Transport();
        using var session = Session(transport);
        using var lease = await session.Firehose!.EnterOperationAsync(TestContext.Current.CancellationToken);

        var results = await new FlashBundleOrchestrator(session).FlashPackageAsync(
            [first, second], null, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task FlashPackageAsync_FirstProgramRejected_ReturnsFailureAndDoesNotExecuteLaterManifest()
    {
        using var directory = new TempDirectory();
        var source = directory.WriteBytes("source.img", new byte[512]);
        using var transport = Transport(rejectProgram: true);
        using var session = Session(transport);

        var results = await new FlashBundleOrchestrator(session).FlashPackageAsync(
            [PackageOption(directory, "first.xml", 1), PackageOption(directory, "second.xml", 2)],
            null, TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(results).IsSuccess);
        Assert.Single(BundleCommands(transport), command => command == "program");
        using var exclusive = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public async Task FlashPackageAsync_InvalidManifestCount_RejectsWithoutDeviceIo(int count)
    {
        using var transport = Transport();
        using var session = Session(transport);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FlashBundleOrchestrator(session)
            .FlashPackageAsync(Enumerable.Range(0, count).Select(_ => new FlashBundleOptions()).ToArray(),
                null, TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task FlashPackageAsync_MixedDryRun_RejectsBeforeParsing()
    {
        using var transport = Transport();
        using var session = Session(transport);
        await Assert.ThrowsAsync<ArgumentException>(() => new FlashBundleOrchestrator(session)
            .FlashPackageAsync([new() { DryRun = true }, new()], null, TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task FlashPackageAsync_NonfinalUfs_RejectsBeforeDeviceIo()
    {
        using var directory = new TempDirectory();
        var source = directory.WriteBytes("source.img", new byte[512]);
        var first = PackageOption(directory, "first.xml", 1);
        first.SkipUfsProvision = false;
        directory.Write("ufs_provision.xml", """
            <data><ufs type="common" bNumberLU="1"/><ufs type="body" LUNum="0"/><ufs type="epilogue" commit="0"/></data>
            """);
        using var transport = Transport();
        using var session = Session(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => new FlashBundleOrchestrator(session)
            .FlashPackageAsync([first, PackageOption(directory, "second.xml", 2)],
                null, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        using var exclusive = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static FlashBundleOptions PackageOption(TempDirectory directory, string name, int start) => new()
    {
        RawProgramPath = directory.Write(name, $"""
            <data><program filename="source.img" start_sector="{start}" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/></data>
            """),
        ImageDirectory = directory.Path,
        SkipUfsProvision = true
    };

    [Fact]
    public async Task FlashPackageAsync_LaterPatchAndUfsFilesChanged_ExecutesCapturedCommands()
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("source.img", new byte[512]);
        var first = PackageOption(directory, "first.xml", 1);
        var second = PackageOption(directory, "second.xml", 2);
        second.PatchPath = directory.Write("patch.xml", """
            <data><patch filename="DISK" start_sector="2" byte_offset="0" size_in_bytes="4" value="7"/></data>
            """);
        second.SkipUfsProvision = false;
        second.UfsProvisionPath = directory.Write("ufs_provision.xml", """
            <data><ufs type="common" bNumberLU="1"/><ufs type="body" LUNum="0"/><ufs type="epilogue" commit="0"/></data>
            """);
        using var transport = Transport();
        using var session = Session(transport);
        var progress = new InlineProgress(value =>
        {
            if (value.Phase == FlashBundlePhase.Completed)
            {
                File.WriteAllText(second.PatchPath, "invalid XML");
                File.WriteAllText(second.UfsProvisionPath, "invalid XML");
            }
        });

        var results = await new FlashBundleOrchestrator(session).FlashPackageAsync(
            [first, second], progress, TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.IsSuccess, string.Join("; ", result.Errors)));
        Assert.Equal(1, results[1].PatchEntriesProcessed);
        Assert.True(results[1].UfsProvisioned);
        Assert.Equal(new[] { "configure", "getstorageinfo", "getstorageinfo", "getstorageinfo", "program",
            "getstorageinfo", "program", "patch", "ufs", "ufs", "ufs" }, BundleCommands(transport));
        Assert.Contains(transport.Writes, bytes => Encoding.UTF8.GetString(bytes).Contains("value=\"7\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlashPackageAsync_AggregateHandleLimit_RejectsAndReleasesBothManifestSources()
    {
        using var directory = new TempDirectory();
        var entries = new StringBuilder("<data>");
        for (var i = 0; i < 513; i++)
        {
            directory.WriteBytes($"{i}.img", new byte[512]);
            entries.Append($"<program filename='{i}.img' start_sector='0' num_partition_sectors='1' SECTOR_SIZE_IN_BYTES='512'/>");
        }
        entries.Append("</data>");
        var path = directory.Write("rawprogram.xml", entries.ToString());
        var options = new FlashBundleOptions
        {
            RawProgramPath = path,
            ImageDirectory = directory.Path,
            DryRun = true,
            SkipUfsProvision = true
        };
        using var transport = Transport();
        using var session = Session(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => new FlashBundleOrchestrator(session)
            .FlashPackageAsync([options, options], null, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        using var first = File.Open(Path.Combine(directory.Path, "0.img"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var last = File.Open(Path.Combine(directory.Path, "512.img"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task FlashPackageAsync_AggregateEntryLimit_EnforcesBoundaryBeforeDeviceIo(int manifests)
    {
        using var directory = new TempDirectory();
        var xml = "<data>" + string.Concat(Enumerable.Repeat(
            "<program filename='' start_sector='0' num_partition_sectors='0' SECTOR_SIZE_IN_BYTES='512'/>", 16384)) + "</data>";
        var path = directory.Write("rawprogram.xml", xml);
        var options = Enumerable.Range(0, manifests).Select(_ => new FlashBundleOptions
        {
            RawProgramPath = path,
            ImageDirectory = directory.Path,
            DryRun = true,
            SkipUfsProvision = true
        }).ToArray();
        using var transport = Transport();
        using var session = Session(transport);
        var operation = new FlashBundleOrchestrator(session).FlashPackageAsync(
            options, null, TestContext.Current.CancellationToken);

        if (manifests == 4)
        {
            Assert.Equal(4, (await operation).Count);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => operation);
        }
        Assert.Empty(transport.Writes);
    }
}