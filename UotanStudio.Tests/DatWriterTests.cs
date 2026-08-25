using UotanStudio.Firmware.Dat;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DAT writer tests: build a minimal raw image, write the DAT pair and verify the transfer list
/// structure plus the byte-exact alignment of the <c>.new.dat</c> content.
/// <para>DAT 写入器测试：构造最小原始镜像，写出 DAT 镜像对并校验 transfer list 结构及
/// <c>.new.dat</c> 内容的逐字节对齐。</para>
/// </summary>
public class DatWriterTests
{
    [Fact]
    public void Write_8192ByteImage_ProducesTwoFullBlocks()
    {
        var image = BuildPattern(8192);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");

        DatWriter.Write(imagePath, datPath, listPath);

        var lines = File.ReadAllLines(listPath);
        Assert.Equal(4, lines.Length);
        Assert.Equal("4", lines[0]);
        Assert.Equal("new", lines[1]);
        Assert.Equal("1", lines[2]);
        Assert.Equal("0 2", lines[3]);
        Assert.Equal(image, File.ReadAllBytes(datPath));
    }

    [Fact]
    public void Write_9000ByteImage_WritesShortFinalBlockAtActualLength()
    {
        var image = BuildPattern(9000);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");

        DatWriter.Write(imagePath, datPath, listPath);

        var lines = File.ReadAllLines(listPath);
        Assert.Equal(4, lines.Length);
        Assert.Equal("4", lines[0]);
        Assert.Equal("new", lines[1]);
        Assert.Equal("1", lines[2]);
        Assert.Equal("0 3", lines[3]);
        Assert.Equal(9000L, new FileInfo(datPath).Length);
        Assert.Equal(image, File.ReadAllBytes(datPath));
    }

    private static byte[] BuildPattern(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
        {
            data[i] = (byte)((i * 7 + 3) & 0xFF);
        }

        return data;
    }
}