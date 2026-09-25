using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public class FlashBundlePreflightTests
{
    private const string Ack = "<data><response value='ACK' rawmode='false'/></data>";

    [Fact]
    public async Task FlashBundleAsync_ProgressMutatesCallerOptions_PreservesOfflinePolicyAndSelections()
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("first.img", new byte[512]);
        var entry = Program("first.img");
        entry.SetAttributeValue("label", "selected");
        var rawPath = directory.Write("rawprogram.xml", new XElement("data", entry).ToString());
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport, 512);
        var options = new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = directory.Path,
            DryRun = true,
            Labels = ["selected"],
            NotLabels = [],
            WipeFirstSectors = 1,
            WipePhysicalPartitions = [0],
            SkipUfsProvision = true
        };
        var progress = new InlineProgress(_ =>
        {
            options.DryRun = false;
            options.RawProgramPath = "missing.xml";
            options.ImageDirectory = "missing";
            options.Labels.Clear();
            options.Labels.Add("other");
            options.NotLabels.Add("selected");
            options.WipePhysicalPartitions[0] = -1;
            options.OverrideSectorSize = 4096;
        });

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            options, progress, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(1, result.ProgramEntriesSkipped);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
        Assert.False(options.DryRun);
    }

    private sealed class InlineProgress(Action<FlashBundleProgress> report) : IProgress<FlashBundleProgress>
    {
        public void Report(FlashBundleProgress value) => report(value);
    }

    [Theory]
    [InlineData("negative-offset")]
    [InlineData("end-offset")]
    [InlineData("overflow-offset")]
    [InlineData("negative-sectors")]
    [InlineData("too-short")]
    [InlineData("partial-sector")]
    [InlineData("invalid-address")]
    [InlineData("relative-range")]
    [InlineData("negative-partition")]
    [InlineData("sector-mismatch")]
    [InlineData("sparse-corrupt")]
    [InlineData("sparse-offset")]
    [InlineData("sparse-size")]
    public async Task FlashBundleAsync_InvalidLaterSource_RejectsBeforeInitializationOrEarlierErase(string failure)
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("first.img", new byte[512]);
        directory.WriteBytes("second.img", new byte[512]);
        var second = Program("second.img");
        switch (failure)
        {
            case "negative-offset": second.SetAttributeValue("file_sector_offset", -1); break;
            case "end-offset": second.SetAttributeValue("file_sector_offset", 1); break;
            case "overflow-offset": second.SetAttributeValue("file_sector_offset", long.MaxValue); break;
            case "negative-sectors": second.SetAttributeValue("num_partition_sectors", -1); break;
            case "too-short": second.SetAttributeValue("num_partition_sectors", 2); break;
            case "partial-sector": directory.WriteBytes("second.img", new byte[511]); break;
            case "invalid-address": second.SetAttributeValue("start_sector", "unexpected"); break;
            case "relative-range": second.SetAttributeValue("start_sector", "NUM_DISK_SECTORS-1."); second.SetAttributeValue("num_partition_sectors", 2); break;
            case "negative-partition": second.SetAttributeValue("physical_partition_number", -1); break;
            case "sector-mismatch": second.SetAttributeValue("SECTOR_SIZE_IN_BYTES", 4096); break;
            case "sparse-corrupt": second.SetAttributeValue("sparse", true); break;
            case "sparse-offset":
                directory.WriteBytes("second.img", BuildFillSparse());
                second.SetAttributeValue("file_sector_offset", 1);
                break;
            case "sparse-size":
                directory.WriteBytes("second.img", BuildFillSparse());
                second.SetAttributeValue("UNSPARSE_FILE_SIZE", 4096);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(failure));
        }
        var rawPath = directory.Write("rawprogram.xml", new XElement("data", Program(""), Program("first.img"), second).ToString());
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport, 512);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            rawPath, null, directory.Path, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        Assert.Equal(0, result.ProgramEntriesProcessed);
        Assert.Equal(0, result.EraseEntriesProcessed);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Theory]
    [InlineData("patch-offset")]
    [InlineData("patch-size")]
    [InlineData("patch-address")]
    [InlineData("patch-sector")]
    [InlineData("wipe-count")]
    [InlineData("wipe-partition")]
    public async Task FlashBundleAsync_InvalidLaterPhase_RejectsBeforeProgramming(string failure)
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("first.img", new byte[512]);
        var rawPath = directory.Write("rawprogram.xml", new XElement("data", Program("first.img")).ToString());
        var patch = new XElement("patch", new XAttribute("filename", "DISK"),
            new XAttribute("start_sector", "0"), new XAttribute("byte_offset", 0),
            new XAttribute("size_in_bytes", 4), new XAttribute("value", "1"));
        switch (failure)
        {
            case "patch-offset": patch.SetAttributeValue("byte_offset", 510); break;
            case "patch-size": patch.SetAttributeValue("size_in_bytes", 9); break;
            case "patch-address": patch.SetAttributeValue("start_sector", "-1"); break;
            case "patch-sector": patch.SetAttributeValue("SECTOR_SIZE_IN_BYTES", 4096); break;
        }
        var patchPath = directory.Write("patch.xml", new XElement("patches", patch).ToString());
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport, 512);
        var options = new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            PatchPath = patchPath,
            ImageDirectory = directory.Path,
            WipeFirstSectors = failure == "wipe-count" ? -1 : 1,
            WipePhysicalPartitions = failure == "wipe-partition" ? [-1] : [0]
        };

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(options, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FlashBundleAsync_UnknownSectorSize_RequiresOfflineEvidenceOrPreflightsAfterConfigure(bool dryRun)
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("first.img", new byte[511]);
        var entry = Program("first.img");
        entry.SetAttributeValue("SECTOR_SIZE_IN_BYTES", 0);
        var rawPath = directory.Write("rawprogram.xml", new XElement("data", entry).ToString());
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<?xml version='1.0'?><data></data>".PadRight(48));
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(Ack)];
        using var session = CreateSession(transport, 0);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = directory.Path,
            DryRun = dryRun
        }, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        if (dryRun)
        {
            Assert.Empty(transport.Writes);
            Assert.Equal(0, transport.ReadCount);
        }
        else
        {
            Assert.Contains("<configure ", Encoding.UTF8.GetString(Assert.Single(transport.Writes)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FlashBundleAsync_NegotiatedSectorMismatch_RejectsBeforeDestructiveCommands()
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("first.img", new byte[512]);
        var rawPath = directory.Write("rawprogram.xml", new XElement("data", Program("first.img")).ToString());
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<?xml version='1.0'?><data></data>".PadRight(48));
        transport.OnWrite = bytes => [Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Contains("<getstorageinfo ", StringComparison.Ordinal)
            ? "<data><response value='ACK' sector_size='4096'/></data>" : Ack)];
        using var session = new EdlSession(transport, new FirehoseConfiguration());

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            rawPath, null, directory.Path, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Contains("<getstorageinfo ", Encoding.UTF8.GetString(transport.Writes[1]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FlashBundleAsync_ExcludedInvalidEntry_DoesNotInvalidateSelectedPlan(bool skipProgram)
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("first.img", new byte[512]);
        var invalid = Program("missing.img");
        invalid.SetAttributeValue("file_sector_offset", -1);
        var rawPath = directory.Write("rawprogram.xml", new XElement("data", Program("first.img"), invalid).ToString());
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport, skipProgram ? 0 : 512);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = directory.Path,
            DryRun = true,
            SkipProgram = skipProgram,
            Labels = skipProgram ? null : ["first.img"]
        }, null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task FlashBundleAsync_ExplicitSectorOverride_ValidatesAgainstRequestedUnits()
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("first.img", new byte[512]);
        var entry = Program("first.img");
        entry.SetAttributeValue("SECTOR_SIZE_IN_BYTES", 4096);
        var rawPath = directory.Write("rawprogram.xml", new XElement("data", entry).ToString());
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport, 4096);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = directory.Path,
            DryRun = true,
            OverrideSectorSize = 512
        }, null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
        Assert.Equal(4096, session.Firehose!.Configuration.SectorSize);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("explicit")]
    [InlineData("skipped")]
    [InlineData("single")]
    public async Task FlashBundleAsync_UfsDiscovery_RequiresUnambiguousOrExplicitSelection(string selection)
    {
        using var directory = new TempDirectory();
        var rawPath = directory.Write("rawprogram.xml", "<data/>");
        var first = directory.Write("a_ufs_provision.xml", """
            <data>
              <ufs type="common" bNumberLU="1"/>
              <ufs type="body" LUNum="0"/>
              <ufs type="epilogue" commit="0"/>
            </data>
            """);
        if (selection != "single")
        {
            directory.Write("b_ufs_provision.xml", "Invalid XML must not be chosen implicitly.");
        }
        using var transport = new ScriptedCommTransport();
        using var session = CreateSession(transport, 512);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = directory.Path,
            DryRun = true,
            UfsProvisionPath = selection == "explicit" ? first : null,
            SkipUfsProvision = selection == "skipped"
        }, null, TestContext.Current.CancellationToken);

        Assert.Equal(selection != "ambiguous", result.IsSuccess);
        if (selection == "ambiguous")
        {
            Assert.Contains(result.Errors, error => error.Contains("select UfsProvisionPath explicitly", StringComparison.Ordinal));
        }
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    private static EdlSession CreateSession(ScriptedCommTransport transport, long sectorSize) => new(transport, new FirehoseConfiguration
    {
        SectorSize = sectorSize,
        AutoDetectSectorSize = false
    });

    private static XElement Program(string filename) => new("program",
        new XAttribute("filename", filename), new XAttribute("label", filename),
        new XAttribute("SECTOR_SIZE_IN_BYTES", 512), new XAttribute("file_sector_offset", 0),
        new XAttribute("num_partition_sectors", 1), new XAttribute("physical_partition_number", 0),
        new XAttribute("start_sector", 0));

    private static byte[] BuildFillSparse()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
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
        writer.Flush();
        return stream.ToArray();
    }
}