using UotanStudio.Firmware.Dat;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DAT writer tests: build raw images and verify the v4 transfer list structure (version, block counts,
/// <c>new</c>/<c>zero</c> commands, comma-separated range sets) plus the byte layout of the <c>.new.dat</c>.
/// <para>DAT 写入器测试：构造原始镜像并校验 v4 transfer list 结构（版本、块数、<c>new</c>/<c>zero</c> 命令、
/// 逗号分隔范围集）以及 <c>.new.dat</c> 的字节布局。</para>
/// </summary>
public class DatWriterTests
{
    [Fact]
    public void Write_AllDataImage_ProducesSingleNewCommand()
    {
        var image = BuildPattern(8192, 7);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");

        DatWriter.Write(imagePath, datPath, listPath);

        var lines = File.ReadAllLines(listPath);
        Assert.Equal(new[] { "4", "2", "0", "0", "new 1,0,2" }, lines);
        Assert.Equal(image, File.ReadAllBytes(datPath));
    }

    [Fact]
    public void Write_ImageWithZeroGap_SplitsNewAndZeroCommands()
    {
        // 块 0-1 数据、块 2 全零、块 3-4 数据。
        // Blocks 0-1 data, block 2 all-zero, blocks 3-4 data.
        var image = new byte[5 * DatWriter.BlockSize];
        Array.Copy(BuildPattern(2 * DatWriter.BlockSize, 7), 0, image, 0, 2 * DatWriter.BlockSize);
        Array.Copy(BuildPattern(2 * DatWriter.BlockSize, 3), 0, image, 3 * DatWriter.BlockSize, 2 * DatWriter.BlockSize);

        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");

        DatWriter.Write(imagePath, datPath, listPath);

        var lines = File.ReadAllLines(listPath);
        Assert.Equal("4", lines[0]);
        Assert.Equal("5", lines[1]);
        Assert.Equal(new[] { "new 2,0,2,3,5", "zero 1,2,3" }, lines.Skip(4));

        // .new.dat 只含 4 个数据块。
        // The .new.dat holds only the 4 data blocks.
        var expectedData = new byte[4 * DatWriter.BlockSize];
        Array.Copy(image, 0, expectedData, 0, 2 * DatWriter.BlockSize);
        Array.Copy(image, 3 * DatWriter.BlockSize, expectedData, 2 * DatWriter.BlockSize, 2 * DatWriter.BlockSize);
        Assert.Equal(expectedData, File.ReadAllBytes(datPath));
    }

    [Fact]
    public void Write_AllZeroImage_ProducesOnlyZeroCommand()
    {
        var image = new byte[3 * DatWriter.BlockSize];
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");

        DatWriter.Write(imagePath, datPath, listPath);

        var lines = File.ReadAllLines(listPath);
        Assert.Equal(new[] { "4", "3", "0", "0", "zero 1,0,3" }, lines);
        Assert.Equal(0L, new FileInfo(datPath).Length);
    }

    [Fact]
    public void Write_NonAlignedImage_PadsFinalBlock()
    {
        var image = BuildPattern(9000, 5);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");

        DatWriter.Write(imagePath, datPath, listPath);

        var lines = File.ReadAllLines(listPath);
        Assert.Equal("4", lines[0]);
        Assert.Equal("3", lines[1]);
        Assert.Equal("new 1,0,3", lines[4]);

        // 末块不足 4096 时以零填充。
        // The final partial block is zero-padded to 4096.
        var expected = new byte[3 * DatWriter.BlockSize];
        Array.Copy(image, expected, image.Length);
        Assert.Equal(expected, File.ReadAllBytes(datPath));
    }

    private static byte[] BuildPattern(int size, int factor)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
        {
            data[i] = (byte)((i * factor + 3) & 0xFF);
        }

        return data;
    }
}