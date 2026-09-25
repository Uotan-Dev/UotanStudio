using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

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

        var byLabel = FlashService.FindEdlTarget(entries, "boot", null);
        Assert.NotNull(byLabel);
        Assert.Equal("boot", byLabel!.Label);

        var byFilename = FlashService.FindEdlTarget(entries, "recovery.img", null);
        Assert.NotNull(byFilename);
        Assert.Equal("recovery", byFilename!.Label);

        var byImagePath = FlashService.FindEdlTarget(entries, "whatever", "/firmware/system.img");
        Assert.NotNull(byImagePath);
        Assert.Equal("system", byImagePath!.Label);

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
        dir.WriteBytes("system.img", new byte[4096 * 8]);
        dir.WriteBytes("recovery.img", new byte[4096 * 4]);

        var entries = RawProgramParser.ParseAll([rawPath], dir.Path);
        var boot = FlashService.FindEdlTarget(entries, "boot", null);
        Assert.NotNull(boot);

        var transport = new FakeCommTransport();
        transport.Enqueue(StorageReply(4096, 1048576));
        transport.Enqueue("<data><response value=\"ACK\" rawmode=\"true\"/></data>");
        transport.Enqueue(AckXml);
        var firehose = new FirehoseProtocol(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            AutoDetectSectorSize = false
        });

        var result = await FlashService.Default.FlashEdlPartitionAsync(firehose, boot!, dir.Path, progress: null, CancellationToken.None);

        Assert.True(result.Success, result.Summary);

        var allWrites = string.Concat(transport.Writes.Select(w => Encoding.UTF8.GetString(w)));
        Assert.Equal(1, CountOccurrences(allWrites, "<program"));
        Assert.Contains("boot.img", allWrites, StringComparison.Ordinal);
        Assert.DoesNotContain("system.img", allWrites, StringComparison.Ordinal);
        Assert.DoesNotContain("recovery.img", allWrites, StringComparison.Ordinal);

        Assert.Equal(bootImage, transport.Writes[2]);
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

        var result = await FlashService.Default.FlashImageAsync(device, "boot", imagePath, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("rawprogram", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1L)]
    [InlineData(2L)]
    [InlineData(long.MaxValue)]
    public async Task FlashEdlPartitionAsync_InvalidSourceOffset_RejectsBeforeWriting(long offset)
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[512]);
        using var transport = new ScriptedCommTransport();
        using var firehose = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });
        var entry = new RawProgramEntry
        {
            Filename = "boot.img",
            StartSector = "0",
            FileSectorOffset = offset,
            NumPartitionSectors = 1
        };

        if (offset == long.MaxValue)
        {
            await Assert.ThrowsAsync<OverflowException>(() => FlashService.Default.FlashEdlPartitionAsync(
                firehose, entry, dir.Path, null, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => FlashService.Default.FlashEdlPartitionAsync(
                firehose, entry, dir.Path, null, TestContext.Current.CancellationToken));
        }

        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task FlashEdlPartitionAsync_ValidOffsetAndDiskRelativeTarget_WritesSelectedSourceRange()
    {
        using var dir = new TempDirectory();
        var expected = Enumerable.Repeat((byte)0x42, 512).ToArray();
        dir.WriteBytes("boot.img", [.. new byte[512], .. expected]);
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(transport.Writes.Count switch
        {
            1 => StorageReply(512, 1024),
            2 => "<data><response value='ACK' rawmode='true'/></data>",
            _ => AckXml
        })];
        using var firehose = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });
        var entry = new RawProgramEntry
        {
            Filename = "boot.img",
            StartSector = "NUM_DISK_SECTORS-1.",
            FileSectorOffset = 1,
            NumPartitionSectors = 1
        };

        var result = await FlashService.Default.FlashEdlPartitionAsync(
            firehose, entry, dir.Path, null, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("start_sector=\"NUM_DISK_SECTORS-1.\"", Encoding.UTF8.GetString(transport.Writes[1]), StringComparison.Ordinal);
        Assert.Equal(expected, transport.Writes[2]);
        Assert.Equal(3, transport.Writes.Count);
    }

    [Theory]
    [InlineData(512, 1)]
    [InlineData(4096, 1024)]
    public async Task FlashEdlPartitionAsync_IncompatibleGeometry_RejectsBeforeProgram(int blockSize, int blocks)
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[1024]);
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(StorageReply(blockSize, blocks))];
        using var firehose = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });
        var entry = new RawProgramEntry { Filename = "boot.img", StartSector = "0", NumPartitionSectors = 2 };

        await Assert.ThrowsAnyAsync<Exception>(() => FlashService.Default.FlashEdlPartitionAsync(
            firehose, entry, dir.Path, null, TestContext.Current.CancellationToken));

        Assert.Single(transport.Writes);
        Assert.Contains("<getstorageinfo", Encoding.UTF8.GetString(transport.Writes[0]), StringComparison.Ordinal);
        Assert.Equal(512, firehose.Configuration.SectorSize);
    }

    [Fact]
    public async Task FlashEdlPartitionAsync_ConflictingManifest_RejectsWithoutChangingConfiguration()
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", new byte[4096]);
        using var transport = new ScriptedCommTransport();
        using var firehose = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });
        var entry = new RawProgramEntry
        {
            Filename = "boot.img",
            StartSector = "0",
            NumPartitionSectors = 1,
            SectorSizeInBytes = 4096
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => FlashService.Default.FlashEdlPartitionAsync(
            firehose, entry, dir.Path, null, TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
        Assert.Equal(512, firehose.Configuration.SectorSize);
    }

    private static string StorageReply(int blockSize, int blocks) =>
        $$$"""<data><log value='{"storage_info":{"block_size":{{{blockSize}}},"total_blocks":{{{blocks}}}}}'/><response value="ACK" rawmode="false"/></data>""";

    [Theory]
    [InlineData("0")]
    [InlineData("NUM_DISK_SECTORS-1.")]
    public async Task FlashEdlPartitionAsync_SparseExceedsDeviceCapacity_RejectsBeforeProgram(string start)
    {
        using var dir = new TempDirectory();
        dir.WriteBytes("boot.img", Convert.FromHexString(
            "3AFF26ED010000001C000C0000020000020000000100000000000000C3CA0000020000000C000000"));
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(StorageReply(512, 1))];
        using var firehose = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });
        var entry = new RawProgramEntry { Filename = "boot.img", StartSector = start };

        await Assert.ThrowsAnyAsync<Exception>(() => FlashService.Default.FlashEdlPartitionAsync(
            firehose, entry, dir.Path, null, TestContext.Current.CancellationToken));

        Assert.Single(transport.Writes);
        Assert.Contains("<getstorageinfo", Encoding.UTF8.GetString(transport.Writes[0]), StringComparison.Ordinal);
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