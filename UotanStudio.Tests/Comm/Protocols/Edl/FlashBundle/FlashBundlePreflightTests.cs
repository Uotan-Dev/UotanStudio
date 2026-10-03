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
    [InlineData("invalid-address")]
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
            case "invalid-address": second.SetAttributeValue("start_sector", "unexpected"); break;
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
    [InlineData("capacity")]
    [InlineData("partial-tail")]
    [InlineData("disk-relative")]
    public async Task FlashBundleAsync_SourceShorterThanDeclaredCapacity_ProgramsSourceDerivedRange(string shape)
    {
        using var directory = new TempDirectory();
        var imageLength = shape == "partial-tail" ? 511 : 512;
        directory.WriteBytes("first.img", new byte[imageLength]);
        var entry = Program("first.img");
        entry.SetAttributeValue("num_partition_sectors", shape == "partial-tail" ? 1 : 2);
        if (shape == "disk-relative")
        {
            entry.SetAttributeValue("start_sector", "NUM_DISK_SECTORS-1.");
        }

        var rawPath = directory.Write("rawprogram.xml", new XElement("data", entry).ToString());
        var programSeen = false;
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("<getstorageinfo", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes("""
                    <data><log value='{"storage_info":{"block_size":512,"total_blocks":1048576}}'/><response value="ACK"/></data>
                    """)];
            }

            if (text.Contains("<configure", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes("""
                    <data><response value="ACK" rawmode="false" MaxPayloadSizeToTargetInBytes="1048576" Program="true" Erase="true" Patch="true" Wipe="true" UfsProvisioning="true"/></data>
                    """)];
            }

            if (text.Contains("<program", StringComparison.Ordinal))
            {
                programSeen = true;
                return [Encoding.UTF8.GetBytes("<data><response value='ACK' rawmode='true'/></data>"), Encoding.UTF8.GetBytes(Ack)];
            }

            return [Encoding.UTF8.GetBytes(Ack)];
        };
        using var session = CreateSession(transport, 512);

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            rawPath, null, directory.Path, null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.True(programSeen);
        var program = Encoding.UTF8.GetString(transport.Writes.Single(write =>
            Encoding.UTF8.GetString(write).Contains("<program", StringComparison.Ordinal)));
        Assert.Contains("num_partition_sectors=\"1\"", program, StringComparison.Ordinal);
        Assert.Contains("start_sector=", program, StringComparison.Ordinal);
        byte[] expectedPayload = [.. new byte[imageLength], .. new byte[512 - imageLength]];
        Assert.Equal(expectedPayload, transport.Writes[^1]);
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
        directory.WriteBytes("first.img", []);
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

    [Theory]
    [InlineData("CON", "reserved device name")]
    [InlineData("nul.img", "reserved device name")]
    [InlineData("nested/LPT9.bin", "reserved device name")]
    [InlineData("aux", "reserved device name")]
    [InlineData("trailing.", "not portable across hosts")]
    [InlineData("trailing ", "not portable across hosts")]
    [InlineData("control\tname", "not portable across hosts")]
    [InlineData("nul\0name", "not portable across hosts")]
    public void ResolveImage_UnsafeMemberName_RejectsBeforeTouchingDisk(string filename, string expected)
    {
        using var directory = new TempDirectory();

        var error = Assert.Throws<EdlException>(() => FlashBundlePath.ResolveImage(directory.Path, filename));

        Assert.Equal(EdlErrorCode.InvalidParameter, error.ErrorCode);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prog.img:backup")]
    [InlineData("C:/windows/win.ini")]
    [InlineData("\\\\?\\C:\\prog.img")]
    [InlineData("/etc/passwd")]
    public void ResolveImage_NonRelativeOrStreamedMember_Rejects(string filename)
    {
        using var directory = new TempDirectory();

        var error = Assert.Throws<EdlException>(() => FlashBundlePath.ResolveImage(directory.Path, filename));

        Assert.Equal(EdlErrorCode.InvalidParameter, error.ErrorCode);
        Assert.Contains("relative to its firmware directory", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.img")]
    [InlineData("nested/../escape.img")]
    [InlineData("folder//name.img")]
    public void ResolveImage_EscapingOrEmptyComponent_Rejects(string filename)
    {
        using var directory = new TempDirectory();

        var error = Assert.Throws<EdlException>(() => FlashBundlePath.ResolveImage(directory.Path, filename));

        Assert.Equal(EdlErrorCode.InvalidParameter, error.ErrorCode);
        Assert.Contains("invalid path component", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("system.img")]
    [InlineData("COM10.img")]
    [InlineData("images/super.img")]
    public void ResolveImage_PortableMemberName_ResolvesInsideTheFirmwareDirectory(string filename)
    {
        using var directory = new TempDirectory();

        var resolved = FlashBundlePath.ResolveImage(directory.Path, filename);

        Assert.Equal(Path.Combine(directory.Path, filename.Replace('/', Path.DirectorySeparatorChar)), resolved);
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