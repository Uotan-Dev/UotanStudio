using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public partial class FlashBundleSourceTests
{
    [Fact]
    public void Get_PathChangedAfterOpen_UsesRetainedFileAndResetsSharedPosition()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteBytes("source.img", new byte[512]);
        var first = Entry(path);
        var second = Entry(path);
        using var sources = FlashBundleSources.Open([first, second], TestContext.Current.CancellationToken);
        var original = sources.Get(first);
        original.Position = 100;
        first.SourceFile = Path.Combine(directory.Path, "missing.img");

        Assert.Same(original, sources.Get(first));
        Assert.Equal(0, original.Position);
        Assert.Same(original, sources.Get(second));
        FlashBundlePreflight.Validate([first, second], null, new FlashBundleOptions(), 512,
            TestContext.Current.CancellationToken, sources);
        Assert.True(original.CanRead);
        sources.Dispose();
        Assert.False(original.CanRead);
        Assert.Throws<ObjectDisposedException>(() => sources.Get(first));
    }

    [Fact]
    public void Open_LaterFileMissing_ReleasesEarlierHandle()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteBytes("source.img", new byte[512]);
        Assert.Throws<FileNotFoundException>(() => FlashBundleSources.Open(
            [Entry(path), Entry(Path.Combine(directory.Path, "missing.img"))], TestContext.Current.CancellationToken));
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(512, exclusive.Length);
    }

    [Fact]
    public void Open_CancelledBetweenFiles_ReleasesEarlierHandle()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteBytes("source.img", new byte[512]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Assert.ThrowsAny<OperationCanceledException>(() => FlashBundleSources.Open(Entries(), cancellation.Token));
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(512, exclusive.Length);

        IEnumerable<RawProgramEntry> Entries()
        {
            yield return Entry(path);
            cancellation.Cancel();
            yield return Entry(path);
        }
    }

    [Fact]
    public void Open_TooManyDistinctSources_RejectsAndReleasesHandles()
    {
        using var directory = new TempDirectory();
        var entries = new List<RawProgramEntry>();
        for (var index = 0; index < 1024; index++)
        {
            entries.Add(Entry(directory.WriteBytes($"{index}.img", [])));
        }
        entries.Add(Entry(Path.Combine(directory.Path, "not-opened.img")));

        Assert.Throws<InvalidDataException>(() => FlashBundleSources.Open(entries, TestContext.Current.CancellationToken));

        using var first = File.Open(entries[0].SourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var last = File.Open(entries[1023].SourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task FlashBundleAsync_SharedSparseImage_ReusesRetainedSourceAcrossValidationAndPrograms()
    {
        using var directory = new TempDirectory();
        using var sparse = new MemoryStream();
        using (var writer = new BinaryWriter(sparse, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0xED26FF3Au);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write((ushort)28);
            writer.Write((ushort)12);
            writer.Write(512u);
            writer.Write(1u);
            writer.Write(1u);
            writer.Write(0u);
            writer.Write((ushort)0xCAC2);
            writer.Write((ushort)0);
            writer.Write(1u);
            writer.Write(16u);
            writer.Write(0x12345678u);
        }
        var path = directory.WriteBytes("source.img", sparse.ToArray());
        var manifest = directory.Write("rawprogram.xml", """
            <data>
              <program filename="source.img" start_sector="0" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/>
              <program filename="source.img" start_sector="1" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/>
            </data>
            """);
        using var transport = Transport();
        using var session = Session(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            manifest, null, directory.Path, null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        var payloads = transport.Writes.Where(value => value.Length == 512).ToArray();
        Assert.Equal(2, payloads.Length);
        var expected = Enumerable.Range(0, 512).Select(index => new byte[] { 0x78, 0x56, 0x34, 0x12 }[index % 4]).ToArray();
        Assert.All(payloads, payload => Assert.Equal(expected, payload));
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlashBundleAsync_SharedImageEntries_ResetSourcePositionAndReleaseHandle(bool dryRun)
    {
        using var directory = new TempDirectory();
        var first = Enumerable.Repeat((byte)0x33, 512).ToArray();
        var second = Enumerable.Repeat((byte)0x5A, 512).ToArray();
        var path = directory.WriteBytes("source.img", [.. first, .. second]);
        var manifest = directory.Write("rawprogram.xml", """
            <data>
              <program filename="source.img" start_sector="0" file_sector_offset="1" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/>
              <program filename="source.img" start_sector="1" file_sector_offset="0" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/>
            </data>
            """);
        using var transport = Transport();
        using var session = Session(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = manifest,
            ImageDirectory = directory.Path,
            DryRun = dryRun
        }, null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(dryRun ? 0 : 1024, result.TotalBytesWritten);
        if (dryRun)
        {
            Assert.Empty(transport.Writes);
        }
        else
        {
            var payloads = transport.Writes.Where(bytes => bytes.Length == 512).ToArray();
            Assert.Equal(2, payloads.Length);
            Assert.Equal(second, payloads[0]);
            Assert.Equal(first, payloads[1]);
        }
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(1024, exclusive.Length);
    }

    [Fact]
    public async Task FlashBundleAsync_PathReplacementOrExclusiveOpen_DoesNotSubstituteSource()
    {
        using var directory = new TempDirectory();
        var bytes = Enumerable.Repeat((byte)0x42, 512).ToArray();
        var path = directory.WriteBytes("source.img", bytes);
        var manifest = Manifest(directory);
        using var transport = Transport();
        using var session = Session(transport);
        var inspected = false;
        var progress = new InlineProgress(value =>
        {
            if (value.Phase != FlashBundlePhase.Programming)
            {
                return;
            }
            inspected = true;
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            }
            else
            {
                File.Move(path, Path.Combine(directory.Path, "retained.img"));
                directory.WriteBytes("source.img", new byte[512]);
            }
        });

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            manifest, null, directory.Path, progress, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.True(inspected);
        Assert.Equal(bytes, Assert.Single(transport.Writes, value => value.Length == 512));
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlashBundleAsync_FailureOrCancellation_ReleasesSources(bool cancel)
    {
        using var directory = new TempDirectory();
        var path = directory.WriteBytes("source.img", new byte[512]);
        var manifest = Manifest(directory);
        using var transport = Transport(rejectProgram: !cancel);
        using var session = Session(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new InlineProgress(value =>
        {
            if (cancel && value.Phase == FlashBundlePhase.Programming)
            {
                cancellation.Cancel();
            }
        });
        var operation = new FlashBundleOrchestrator(session).FlashBundleAsync(manifest, null, directory.Path, progress, cancellation.Token);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            Assert.False((await operation).IsSuccess);
        }
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(512, exclusive.Length);
    }

    [Fact]
    public async Task FlashBundleAsync_LaterPreflightFailure_ReleasesAllSourcesBeforeReturning()
    {
        using var directory = new TempDirectory();
        var first = directory.WriteBytes("source.img", new byte[512]);
        var second = directory.WriteBytes("short.img", new byte[511]);
        var manifest = directory.Write("rawprogram.xml", """
            <data>
              <program filename="source.img" start_sector="0" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/>
              <program filename="short.img" start_sector="1" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/>
            </data>
            """);
        using var transport = Transport();
        using var session = Session(transport);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            manifest, null, directory.Path, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(transport.Writes);
        using var firstExclusive = File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var secondExclusive = File.Open(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static RawProgramEntry Entry(string path) => new()
    {
        Filename = Path.GetFileName(path),
        SourceFile = path,
        NumPartitionSectors = 1,
        SectorSizeInBytes = 512
    };

    private static string Manifest(TempDirectory directory) => directory.Write("rawprogram.xml", """
        <data><program filename="source.img" start_sector="0" num_partition_sectors="1" SECTOR_SIZE_IN_BYTES="512"/></data>
        """);

    private static EdlSession Session(ScriptedCommTransport transport) => new(transport,
        new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });

    private static ScriptedCommTransport Transport(bool rejectProgram = false)
    {
        const string ack = "<data><response value='ACK' rawmode='false'/></data>";
        const string configureAck = """
            <data><response value='ACK' rawmode='false' MaxPayloadSizeToTargetInBytes='1048576' Erase='true' Patch='true' Wipe='true' UfsProvisioning='true'/></data>
            """;
        var transport = new ScriptedCommTransport();
        transport.Enqueue("<?xml version='1.0'?><data></data>".PadRight(48));
        transport.OnWrite = bytes =>
        {
            if (bytes[0] != '<')
            {
                return [Encoding.UTF8.GetBytes(ack)];
            }
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single().Name.LocalName;
            var response = command switch
            {
                "configure" => configureAck,
                "getstorageinfo" => """<data><log value='{"storage_info":{"block_size":512,"total_blocks":100}}'/><response value='ACK'/></data>""",
                "program" => rejectProgram ? "<data><response value='NAK'/></data>"
                    : "<data><response value='ACK' rawmode='true'/></data>",
                _ => ack
            };
            return [Encoding.UTF8.GetBytes(response)];
        };
        return transport;
    }

    private sealed class InlineProgress(Action<FlashBundleProgress> report) : IProgress<FlashBundleProgress>
    {
        public void Report(FlashBundleProgress value) => report(value);
    }
}