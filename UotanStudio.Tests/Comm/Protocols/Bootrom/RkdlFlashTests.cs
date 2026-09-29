using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Bootrom;

/// <summary>
/// RKDL flash tests: Rockchip parameter parsing and the WRITE_LBA command sequence.
/// <para>RKDL 刷写测试：Rockchip parameter 解析与 WRITE_LBA 命令序列。</para>
/// </summary>
public class RkdlFlashTests
{
    private const string ParameterFile = """
        FIRMWARE_VER: 8.1
        MACHINE_MODEL: RK3568
        MACHINE_ID: 007
        MANUFACTURER: Rockchip
        MAGIC: 0x5041524B
        ATAG: 0x00200800
        MACHINE: 0xffffffff
        CHECK_MASK: 0x80
        PWR_HLD: 0,0,A,0,1
        CMDLINE: mtdparts=rk29xxnand:0x00002000@0x00002000(uboot),0x00002000@0x00004000(trust),0x00002000@0x00006000(boot),0x00010000@0x00008000(system):gpt
        """;

    [Fact]
    public void RockchipParameterParser_ParsesPartitionTable()
    {
        using var dir = new TempDirectory();
        var parameterPath = dir.Write("parameter", ParameterFile);

        var partitions = RockchipParameterParser.Parse(parameterPath);

        Assert.Equal(4, partitions.Count);
        Assert.Equal("uboot", partitions[0].Name);
        Assert.Equal(0x2000, partitions[0].SizeSectors);
        Assert.Equal(0x2000, partitions[0].OffsetSectors);
        Assert.Equal("boot", partitions[2].Name);
        Assert.Equal(0x2000, partitions[2].SizeSectors);
        Assert.Equal(0x6000, partitions[2].OffsetSectors);
        Assert.Equal("system", partitions[3].Name);
        Assert.Equal(0x10000, partitions[3].SizeSectors);
        Assert.Equal(0x8000, partitions[3].OffsetSectors);
    }

    [Fact]
    public void RockchipParameterParser_HandlesSuffixedAndDecimalValues()
    {
        var partitions = RockchipParameterParser.ParseCommandLine(
            "mtdparts=rk29xxnand:64@0x4000(test32k),0x00001000@1048576(testdec):gpt");

        Assert.Equal(2, partitions.Count);
        Assert.Equal(64, partitions[0].SizeSectors);
        Assert.Equal(0x4000, partitions[0].OffsetSectors);
        Assert.Equal(0x1000, partitions[1].SizeSectors);
        Assert.Equal(1048576, partitions[1].OffsetSectors);
    }

    [Fact]
    public void FlashRkdlPartition_WritesImageAtPartitionLba()
    {
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport);

        var imageBytes = new byte[4096];
        for (var i = 0; i < imageBytes.Length; i++)
        {
            imageBytes[i] = (byte)(i * 31);
        }

        using var dir = new TempDirectory();
        var imagePath = dir.WriteBytes("boot.img", imageBytes);
        var partition = new RockchipPartition("boot", sizeSectors: 0x2000, offsetSectors: 0x6000);

        var result = FlashService.FlashRkdlPartition(session, partition, imagePath, progress: null, CancellationToken.None);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(2, transport.Writes.Count);

        var cbw = Assert.Single(transport.Cbws);
        Assert.Equal(31, cbw.RawBytes.Length);
        Assert.Equal((byte)0x15, cbw.Opcode);
        Assert.Equal(0x00, cbw.Flags);
        Assert.Equal(0x0A, cbw.CbwcbLength);
        Assert.Equal(0x6000u, cbw.Address);
        Assert.Equal(8, cbw.SectorLength);
        Assert.Equal((uint)imageBytes.Length, cbw.TransferLength);
        Assert.Equal(cbw.Tag, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[0].AsSpan(4)));

        Assert.Equal(imageBytes, transport.Writes[1]);
    }
}