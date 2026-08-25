using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Core.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm;

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
        // 64 sectors = 32 KB.
        Assert.Equal(64, partitions[0].SizeSectors);
        Assert.Equal(0x4000, partitions[0].OffsetSectors);
        // 1048576 is already expressed in sectors.
        Assert.Equal(0x1000, partitions[1].SizeSectors);
        Assert.Equal(1048576, partitions[1].OffsetSectors);
    }

    [Fact]
    public void FlashRkdlPartition_WritesImageAtPartitionLba()
    {
        var transport = new FakeCommTransport
        {
            OnWrite = bytes => BuildCswResponse(bytes)
        };
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
        Assert.True(transport.Writes.Count >= 2);

        // First write is the WRITE_LBA CBW (opcode 0x15) addressed to LBA 0x6000.
        var cbw = transport.Writes[0];
        Assert.Equal(31, cbw.Length);
        Assert.Equal(RkdlFrame.CbwSignature, cbw[..4]);
        Assert.Equal((byte)RkdlCommand.WriteLba, cbw[15]);
        Assert.Equal(0x00, cbw[17]); // address big-endian
        Assert.Equal(0x00, cbw[18]);
        Assert.Equal(0x60, cbw[19]);
        Assert.Equal(0x00, cbw[20]);

        // Second write is the raw image payload.
        Assert.Equal(imageBytes, transport.Writes[1]);
    }

    private static IEnumerable<byte[]> BuildCswResponse(byte[] data)
    {
        // A 31-byte CBW starting with "USBC" gets a success CSW echoing its tag.
        if (data.Length == RkdlFrame.CbwLength && data[..4].SequenceEqual(RkdlFrame.CbwSignature))
        {
            var csw = new byte[RkdlFrame.CswLength];
            RkdlFrame.CswSignature.CopyTo(csw, 0);
            var tag = BitConverter.ToUInt32(data, 4);
            BitConverter.GetBytes(tag).CopyTo(csw, 4);
            // data residue = 0, status = 0 (success).
            yield return csw;
        }
    }
}