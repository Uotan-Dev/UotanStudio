using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

/// <summary>
/// Firehose program wire tests for manifest entries whose declared sector count is the partition
/// capacity: the payload must carry the source bytes with only the final sector zero-filled, and a
/// source longer than the declared range must be truncated to it.
/// <para>当清单声明扇区数为分区容量时的 Firehose program 线上测试：载荷必须只携带源文件字节并仅零填充最后一个扇区，
/// 长于声明范围的源文件必须被截断到声明范围。</para>
/// </summary>
public class EdlProgramSectorCountTests
{
    private const string AckXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <data>
        <response value="ACK" rawmode="false"/>
        </data>
        """;

    [Fact]
    public async Task FlashEdlPartitionAsync_DeclaredCapacityLargerThanSource_ProgramsSourceSectorsWithZeroFilledTail()
    {
        using var dir = new TempDirectory();
        byte[] source = [.. Enumerable.Repeat((byte)0x5A, 512), .. Enumerable.Repeat((byte)0xA7, 256)];
        dir.WriteBytes("boot.img", source);
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(transport.Writes.Count switch
        {
            1 => StorageReply(512, 8192),
            2 => "<data><response value='ACK' rawmode='true'/></data>",
            _ => AckXml
        })];
        using var firehose = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });
        var entry = new RawProgramEntry
        {
            Filename = "boot.img",
            StartSector = "0",
            NumPartitionSectors = 262144,
            SectorSizeInBytes = 512
        };

        var result = await FlashService.Default.FlashEdlPartitionAsync(
            firehose, entry, dir.Path, null, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var program = Encoding.UTF8.GetString(transport.Writes[1]);
        Assert.Contains("<program", program, StringComparison.Ordinal);
        Assert.Contains("num_partition_sectors=\"2\"", program, StringComparison.Ordinal);
        byte[] expected = [.. source, .. new byte[256]];
        Assert.Equal(expected, transport.Writes[2]);
    }

    [Fact]
    public async Task FlashEdlPartitionAsync_SourceLongerThanDeclaredRange_ProgramsDeclaredSectorsOnly()
    {
        using var dir = new TempDirectory();
        var source = Enumerable.Repeat((byte)0x5A, 1024).ToArray();
        dir.WriteBytes("boot.img", source);
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = _ => [Encoding.UTF8.GetBytes(transport.Writes.Count switch
        {
            1 => StorageReply(512, 8192),
            2 => "<data><response value='ACK' rawmode='true'/></data>",
            _ => AckXml
        })];
        using var firehose = new FirehoseProtocol(transport, new FirehoseConfiguration { SectorSize = 512 });
        var entry = new RawProgramEntry
        {
            Filename = "boot.img",
            StartSector = "0",
            NumPartitionSectors = 1,
            SectorSizeInBytes = 512
        };

        var result = await FlashService.Default.FlashEdlPartitionAsync(
            firehose, entry, dir.Path, null, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("num_partition_sectors=\"1\"", Encoding.UTF8.GetString(transport.Writes[1]), StringComparison.Ordinal);
        byte[] expectedPayload = [.. Enumerable.Repeat((byte)0x5A, 512)];
        Assert.Equal(expectedPayload, transport.Writes[2]);
        Assert.Contains("truncated", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateProgram_SourceShorterThanDeclaredCapacity_ReturnsSourceRangeWithoutThrowing()
    {
        using var dir = new TempDirectory();
        var imagePath = Path.Combine(dir.Path, "boot.img");
        File.WriteAllBytes(imagePath, Enumerable.Repeat((byte)0x11, 4096).ToArray());
        var entry = new RawProgramEntry
        {
            Filename = "boot.img",
            SourceFile = imagePath,
            Label = "boot",
            StartSector = "0x8000",
            NumPartitionSectors = 262144,
            SectorSizeInBytes = 4096
        };
        var options = new FlashBundleOptions
        {
            RawProgramPath = Path.Combine(dir.Path, "rawprogram0.xml"),
            ImageDirectory = dir.Path
        };

        var ranges = await Task.Run(() => FlashBundlePreflight.Validate([entry], null, options, 4096,
            TestContext.Current.CancellationToken));

        var range = Assert.Single(ranges);
        Assert.Equal(32768, range.StartLba);
        Assert.Equal(1, range.BlockCount);
    }

    private static string StorageReply(int blockSize, int blocks) =>
        $$$"""<data><log value='{"storage_info":{"block_size":{{{blockSize}}},"total_blocks":{{{blocks}}}}}'/><response value="ACK" rawmode="false"/></data>""";
}