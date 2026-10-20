using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DAT writer/reader Brotli (<c>.br</c>) and range-splitting tests: verify the plain write → unpack round trip,
/// the Brotli-compressed write → unpack round trip (including a large multi-megabyte image and a non-block-aligned
/// image), and that <see cref="DatWriteOptions.MaxCommandRangeBlocks"/> splits <c>new</c> commands while keeping
/// the unpacked image byte-identical.
/// <para>DAT 写入/读取器的 Brotli（<c>.br</c>）与范围拆分测试：校验普通「写入→解包」往返、Brotli 压缩
/// 「写入→解包」往返（含数 MB 大镜像与非块对齐镜像），以及 <see cref="DatWriteOptions.MaxCommandRangeBlocks"/>
/// 拆分 <c>new</c> 命令后解包镜像仍逐字节一致。</para>
/// </summary>
public class DatBrTests
{
    [Fact]
    public void RoundTrip_PlainDat_MixedImage_IsByteExact()
    {
        RoundTrip(BuildMixedImage(64, 7), brotli: false);
    }

    [Fact]
    public void RoundTrip_BrotliDat_MixedImage_IsByteExact()
    {
        RoundTrip(BuildMixedImage(64, 7), brotli: true);
    }

    [Fact]
    public void RoundTrip_BrotliDat_LargeImage_IsByteExact()
    {
        RoundTrip(BuildMixedImage(300, 3), brotli: true);
    }

    [Fact]
    public void RoundTrip_BrotliDat_NonAlignedImage_IsByteExact()
    {
        var full = BuildMixedImage(20, 4);
        var image = full[..^1000];
        var expected = new byte[full.Length];
        Array.Copy(image, expected, image.Length);
        RoundTrip(image, expected, brotli: true);
    }

    [Fact]
    public void Write_WithProgress_ReportsMonotonicBytesUpToSourceLength()
    {
        var image = BuildMixedImage(64, 7);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");

        var progress = new SyncProgress<(long Processed, long Total)>();
        DatWriter.Write(imagePath, datPath, listPath, new DatWriteOptions(), progress, TestContext.Current.CancellationToken);

        Assert.NotEmpty(progress.Reports);
        Assert.Equal(image.Length, progress.Reports[^1].Total);
        Assert.Equal(image.Length, progress.Reports[^1].Processed);
        for (var i = 1; i < progress.Reports.Count; i++)
            Assert.True(progress.Reports[i].Processed >= progress.Reports[i - 1].Processed, "Progress must not decrease.");
    }

    [Fact]
    public void RoundTrip_MaxCommandRangeBlocks_SplitsNewCommandsAndRoundTrips()
    {
        var image = BuildMixedImage(40, 5);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");

        DatWriter.Write(imagePath, datPath, listPath, new DatWriteOptions { MaxCommandRangeBlocks = 10 },
            cancellationToken: TestContext.Current.CancellationToken);

        var transferList = TransferListParser.Parse(listPath);
        var newCommands = transferList.Commands.Where(c => c.Kind == TransferListCommandKind.New).ToList();
        Assert.True(newCommands.Count > 1);
        foreach (var command in newCommands)
        {
            var blockCount = command.Ranges.Sum(r => r.BlockCount);
            Assert.True(blockCount >= 1 && blockCount <= 10);
        }

        var outputPath = Path.Combine(temp.Path, "output.img");
        DatReader.Unpack(listPath, datPath, outputPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(image, File.ReadAllBytes(outputPath));
    }

    [Fact]
    public void Write_CompressWithBrotli_RemovesUncompressedDat()
    {
        var image = BuildMixedImage(32, 6);
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");

        DatWriter.Write(imagePath, datPath, listPath, new DatWriteOptions { CompressWithBrotli = true },
            cancellationToken: TestContext.Current.CancellationToken);

        var brPath = datPath + ".br";
        Assert.True(File.Exists(brPath));
        Assert.False(File.Exists(datPath));
        Assert.True(new FileInfo(brPath).Length > 0);
    }

    private static void RoundTrip(byte[] image, bool brotli, int maxCommandRangeBlocks = 0)
        => RoundTrip(image, image, brotli, maxCommandRangeBlocks);

    private static void RoundTrip(byte[] image, byte[] expected, bool brotli, int maxCommandRangeBlocks = 0)
    {
        using var temp = new TempDirectory();
        var imagePath = temp.WriteBytes("input.img", image);
        var datPath = Path.Combine(temp.Path, "output.new.dat");
        var listPath = Path.Combine(temp.Path, "output.transfer.list");

        DatWriter.Write(imagePath, datPath, listPath, new DatWriteOptions
        {
            CompressWithBrotli = brotli,
            MaxCommandRangeBlocks = maxCommandRangeBlocks
        });

        var outputPath = Path.Combine(temp.Path, "output.img");
        if (brotli)
        {
            var brPath = datPath + ".br";
            Assert.True(File.Exists(brPath));
            Assert.False(File.Exists(datPath));
            DatReader.Unpack(listPath, brPath, outputPath, brotliCompressed: true);
        }
        else
        {
            DatReader.Unpack(listPath, datPath, outputPath);
        }

        Assert.Equal(expected, File.ReadAllBytes(outputPath));
    }

    private static byte[] BuildMixedImage(int totalBlocks, int dataFactor)
    {
        var data = new byte[totalBlocks * DatWriter.BlockSize];
        for (var block = 0; block < totalBlocks; block++)
        {
            if (block % 3 == 0)
            {
                continue;
            }

            var offset = block * DatWriter.BlockSize;
            for (var i = 0; i < DatWriter.BlockSize; i++)
            {
                data[offset + i] = (byte)((i + (block * 7) + dataFactor) & 0xFF);
            }
        }

        return data;
    }
}