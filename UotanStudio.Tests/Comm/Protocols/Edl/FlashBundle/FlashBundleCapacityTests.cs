using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public class FlashBundleCapacityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreflightDeviceAsync_SelectedDestructivePhases_OnlyInitializesAndQueries(bool invalidRange)
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("first.img", new byte[512]);
        var rawPath = directory.Write("rawprogram.xml", """
            <data>
              <program filename="first.img" start_sector="1" num_partition_sectors="1" physical_partition_number="0"/>
              <program filename="" start_sector="2" num_partition_sectors="1" physical_partition_number="0"/>
            </data>
            """);
        var patchPath = directory.Write("patch.xml",
            "<data><patch filename='DISK' start_sector='0' byte_offset='0' size_in_bytes='4' value='0'/></data>");
        directory.Write("ufs_provision.xml", """
            <data>
              <ufs type="common" bNumberLU="1"/>
              <ufs type="body" LUNum="0"/>
              <ufs type="epilogue" commit="0"/>
            </data>
            """);
        using var transport = CreateTransport("");
        using var session = new EdlSession(transport, new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });
        var options = new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = directory.Path,
            PatchPath = patchPath,
            WipeFirstSectors = invalidRange ? 101 : 1
        };

        var result = await new FlashBundleOrchestrator(session).PreflightDeviceAsync(
            options, TestContext.Current.CancellationToken);

        Assert.Equal(!invalidRange, result.IsSuccess);
        Assert.False(options.DryRun);
        Assert.Equal(0, result.ProgramEntriesProcessed);
        Assert.Equal(0, result.EraseEntriesProcessed);
        Assert.Equal(0, result.PatchEntriesProcessed);
        Assert.Equal(0, result.WipeEntriesProcessed);
        Assert.Equal(0, result.TotalBytesWritten);
        Assert.Equal(new[] { "configure", "getstorageinfo" }, transport.Writes.Select(bytes =>
            XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single().Name.LocalName));
        using var exclusive = File.Open(Path.Combine(directory.Path, "first.img"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("program")]
    [InlineData("relative")]
    [InlineData("patch")]
    [InlineData("wipe")]
    [InlineData("missing")]
    [InlineData("sector-size")]
    public async Task FlashBundleAsync_DeviceRangeInvalid_RejectsBeforeAnyDestructiveCommand(string failure)
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("first.img", new byte[512]);
        directory.WriteBytes("second.img", new byte[1024]);
        var start = failure == "relative" ? "NUM_DISK_SECTORS-101." : failure == "program" ? "99" : "1";
        var rawPath = directory.Write("rawprogram.xml", $"""
            <data>
              <program filename="first.img" start_sector="0" num_partition_sectors="1" physical_partition_number="0"/>
              <program filename="second.img" start_sector="{start}" num_partition_sectors="2" physical_partition_number="1"/>
            </data>
            """);
        var patchPath = failure == "patch" ? directory.Write("patch.xml",
            "<data><patch filename='DISK' start_sector='100' physical_partition_number='1' byte_offset='0' size_in_bytes='4' value='0'/></data>") : null;
        using var transport = CreateTransport(failure);
        using var session = new EdlSession(transport, new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(new FlashBundleOptions
        {
            RawProgramPath = rawPath,
            ImageDirectory = directory.Path,
            PatchPath = patchPath,
            WipeFirstSectors = failure == "wipe" ? 101 : 0
        }, null, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        Assert.Equal(0, result.ProgramEntriesProcessed);
        Assert.All(transport.Writes, bytes =>
        {
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single().Name.LocalName;
            Assert.Contains(command, new[] { "configure", "getstorageinfo" });
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task FlashBundleAsync_ValidRanges_QueriesEachRegionBeforeFirstProgram(int secondPartition)
    {
        using var directory = new TempDirectory();
        directory.WriteBytes("first.img", new byte[512]);
        directory.WriteBytes("second.img", new byte[512]);
        var rawPath = directory.Write("rawprogram.xml", $"""
            <data>
              <program filename="first.img" start_sector="0" num_partition_sectors="1" physical_partition_number="0"/>
              <program filename="second.img" start_sector="NUM_DISK_SECTORS-1." num_partition_sectors="1" physical_partition_number="{secondPartition}"/>
            </data>
            """);
        using var transport = CreateTransport("");
        using var session = new EdlSession(transport, new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false });

        var result = await new FlashBundleOrchestrator(session).FlashBundleAsync(
            rawPath, null, directory.Path, null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        var commands = transport.Writes.Where(bytes => bytes[0] == '<')
            .Select(bytes => XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single().Name.LocalName).ToArray();
        Assert.Equal(secondPartition + 1, commands.Count(command => command == "getstorageinfo"));
        Assert.Equal(secondPartition + 2, Array.IndexOf(commands, "program"));
        Assert.Equal(2, result.ProgramEntriesProcessed);
    }

    private static ScriptedCommTransport CreateTransport(string failure)
    {
        const string ack = "<data><response value='ACK' rawmode='false'/></data>";
        var transport = new ScriptedCommTransport();
        transport.Enqueue("<?xml version='1.0'?><data></data>".PadRight(48));
        transport.OnWrite = bytes =>
        {
            if (bytes[0] != '<')
            {
                return [Encoding.UTF8.GetBytes(ack)];
            }
            var command = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Single();
            if (command.Name == "getstorageinfo")
            {
                if (failure == "missing")
                {
                    return [Encoding.UTF8.GetBytes(ack)];
                }
                var blockSize = failure == "sector-size" ? 4096 : 512;
                return [Encoding.UTF8.GetBytes($$$"""
                    <data><log value='{"storage_info":{"block_size":{{{blockSize}}},"total_blocks":100}}'/><response value='ACK'/></data>
                    """)];
            }
            return [Encoding.UTF8.GetBytes(command.Name == "program"
                ? "<data><response value='ACK' rawmode='true'/></data>" : ack)];
        };
        return transport;
    }
}