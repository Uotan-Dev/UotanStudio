using UotanStudio.Firmware.Dat;
using UotanStudio.Firmware.Sparse;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DAT reader tests: verify the write → unpack round trip is byte-exact for block-aligned images, zero-gap
/// images and sparse <c>.new.dat</c> input, and that incremental commands without a source image are rejected.
/// <para>DAT 读取器测试：校验块对齐镜像、含零块镜像与稀疏 <c>.new.dat</c> 输入的「写入→解包」往返逐字节一致，
/// 且缺少源镜像的增量命令被拒绝。</para>
/// </summary>
public class DatReaderTests
{
    [Fact]
    public void RoundTrip_BlockAlignedImage_IsByteExact()
    {
        RoundTrip(BuildPattern(8 * DatWriter.BlockSize, 7));
    }

    [Fact]
    public void RoundTrip_ImageWithZeroGap_IsByteExact()
    {
        var image = new byte[6 * DatWriter.BlockSize];
        Array.Copy(BuildPattern(2 * DatWriter.BlockSize, 7), 0, image, 0, 2 * DatWriter.BlockSize);
        Array.Copy(BuildPattern(2 * DatWriter.BlockSize, 3), 0, image, 4 * DatWriter.BlockSize, 2 * DatWriter.BlockSize);
        RoundTrip(image);
    }

    [Fact]
    public void Unpack_SparseNewData_IsByteExact()
    {
        var image = BuildPattern(4 * DatWriter.BlockSize, 5);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");
        DatWriter.Write(imagePath, datPath, listPath);

        // 将 .new.dat 转为稀疏格式再解包，验证稀疏输入被透明转换。
        // Convert the .new.dat to sparse, then unpack to verify sparse input is transparently converted.
        var sparseDat = Path.Combine(temp.Path, "output.sparse.dat");
        SparseImageConverter.ConvertRawToSparse(datPath, sparseDat);

        var outputPath = Path.Combine(temp.Path, "output.img");
        DatReader.Unpack(listPath, sparseDat, outputPath);

        Assert.Equal(image, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void Unpack_AllZeroImage_ProducesZeros()
    {
        var image = new byte[3 * DatWriter.BlockSize];
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");
        DatWriter.Write(imagePath, datPath, listPath);

        var outputPath = Path.Combine(temp.Path, "output.img");
        DatReader.Unpack(listPath, datPath, outputPath);

        Assert.Equal(image, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void Unpack_DiffCommand_WithoutSource_ThrowsArgumentException()
    {
        using var temp = new TempDirectory();
        var listPath = temp.Write("x.transfer.list", "4\n4\n0\n0\ndiff 1,0,4\n");
        var datPath = temp.WriteBytes("x.new.dat", []);

        Assert.Throws<ArgumentException>(() => DatReader.Unpack(listPath, datPath, Path.Combine(temp.Path, "out.img")));
    }

    [Fact]
    public void Unpack_StashCommand_WithoutSource_ThrowsArgumentException()
    {
        using var temp = new TempDirectory();
        var listPath = temp.Write("x.transfer.list", "4\n4\n1\n2\nstash abc123 1,0,2\nnew 1,2,4\n");
        var datPath = temp.WriteBytes("x.new.dat", []);

        Assert.Throws<ArgumentException>(() => DatReader.Unpack(listPath, datPath, Path.Combine(temp.Path, "out.img")));
    }

    private static void RoundTrip(byte[] image)
    {
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");
        DatWriter.Write(imagePath, datPath, listPath);

        var outputPath = Path.Combine(temp.Path, "output.img");
        DatReader.Unpack(listPath, datPath, outputPath);

        Assert.Equal(image, File.ReadAllBytes(outputPath));
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