using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm;

/// <summary>
/// EDL single-partition flash tests: rawprogram parsing, partition matching and the
/// Firehose program command sequence (only the target partition is programmed).
/// <para>EDL 单分区直刷测试：rawprogram 解析、分区匹配与 Firehose program 命令序列
/// （只对目标分区发 program）。</para>
/// </summary>
public class EdlSinglePartitionTests
{
    private const string RawProgramXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <data>
          <program SECTOR_SIZE_IN_BYTES="4096" file_sector_offset="0" filename="boot.img"
                   physical_partition_number="0" start_sector="0x8000" num_partition_sectors="4"
                   label="boot" sparse="false" />
          <program SECTOR_SIZE_IN_BYTES="4096" file_sector_offset="0" filename="system.img"
                   physical_partition_number="0" start_sector="0x9000" num_partition_sectors="8"
                   label="system" sparse="false" />
          <program SECTOR_SIZE_IN_BYTES="4096" file_sector_offset="0" filename="recovery.img"
                   physical_partition_number="0" start_sector="0xA000" num_partition_sectors="4"
                   label="recovery" sparse="false" />
        </data>
        """;

    private const string AckXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <data>
        <response value="ACK" rawmode="false"/>
        </data>
        """;

    [Fact]
    public void RawProgramParser_ParsesAllProgramEntries()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", RawProgramXml);

        var entries = RawProgramParser.Parse(rawPath);

        Assert.Equal(3, entries.Count);
        Assert.Equal("boot", entries[0].Label);
        Assert.Equal("boot.img", entries[0].Filename);
        Assert.Equal(0x8000, FlashService.ParseStartSectorValue(entries[0].StartSector));
        Assert.Equal(4, entries[0].NumPartitionSectors);
        Assert.Equal("system", entries[1].Label);
        Assert.Equal("recovery", entries[2].Label);
    }

    [Fact]
    public void FindEdlTarget_MatchesByLabelFilenameAndImagePath()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", RawProgramXml);
        var entries = RawProgramParser.Parse(rawPath);

        // Match by label.
        var byLabel = FlashService.FindEdlTarget(entries, "boot", null);
        Assert.NotNull(byLabel);
        Assert.Equal("boot", byLabel!.Label);

        // Match by filename.
        var byFilename = FlashService.FindEdlTarget(entries, "recovery.img", null);
        Assert.NotNull(byFilename);
        Assert.Equal("recovery", byFilename!.Label);

        // Match by the selected image path.
        var byImagePath = FlashService.FindEdlTarget(entries, "whatever", "/firmware/system.img");
        Assert.NotNull(byImagePath);
        Assert.Equal("system", byImagePath!.Label);

        // Unknown partition never matches.
        Assert.Null(FlashService.FindEdlTarget(entries, "vendor", null));
    }

    [Fact]
    public async Task FlashEdlPartitionAsync_ProgramsOnlyTheTargetPartition()
    {
        using var dir = new TempDirectory();
        var rawPath = dir.Write("rawprogram0.xml", RawProgramXml);
        var bootImage = new byte[4096 * 4];
        Random.Shared.NextBytes(bootImage);
        dir.WriteBytes("boot.img", bootImage);
        // Images referenced by other entries do not need to exist for the single-flash path,
        // but keep the bundle realistic.
        dir.WriteBytes("system.img", new byte[4096 * 8]);
        dir.WriteBytes("recovery.img", new byte[4096 * 4]);

        var entries = RawProgramParser.ParseAll([rawPath], dir.Path);
        var boot = FlashService.FindEdlTarget(entries, "boot", null);
        Assert.NotNull(boot);

        var transport = new FakeCommTransport();
        transport.Enqueue(AckXml);
        var firehose = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            AutoDetectSectorSize = false
        });

        var result = await FlashService.FlashEdlPartitionAsync(firehose, boot!, dir.Path, progress: null, CancellationToken.None);

        Assert.True(result.Success, result.Summary);

        // Exactly one <program> command, targeting boot.img only.
        var allWrites = string.Concat(transport.Writes.Select(w => Encoding.UTF8.GetString(w)));
        Assert.Equal(1, CountOccurrences(allWrites, "<program"));
        Assert.Contains("boot.img", allWrites, StringComparison.Ordinal);
        Assert.DoesNotContain("system.img", allWrites, StringComparison.Ordinal);
        Assert.DoesNotContain("recovery.img", allWrites, StringComparison.Ordinal);

        // The raw payload written after the program XML is exactly the boot image bytes.
        Assert.Equal(bootImage, transport.Writes[1]);
    }

    [Fact]
    public async Task FlashImageAsync_EdlWithoutRawProgram_ReturnsClearError()
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[1024]);
        var imagePath = Path.Combine(dir.Path, "boot.img");

        var device = new UotanStudio.ViewModels.Devices.DeviceInfo(new UotanStudio.Services.Devices.DetectedDevice
        {
            Key = "edl-test",
            Serial = "TEST-EDL",
            Protocol = DeviceProtocolKind.Edl,
            Transport = UotanStudio.Services.Devices.DeviceTransportKind.Usb
        });

        var result = await FlashService.FlashImageAsync(device, "boot", imagePath, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("rawprogram", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}